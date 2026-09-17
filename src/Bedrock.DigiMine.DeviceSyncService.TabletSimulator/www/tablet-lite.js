/* Lite OT tablet — standalone window (Tasks + Ad-Hoc) + Home Open Tablet bridge */
(function (global) {
  'use strict';

  const DEST_TYPES = new Set(['crusher', 'stockpile', 'wastedump', 'waste_dump', 'waste dump']);
  const isStandalone = document.body.classList.contains('tl-page');
  const TZ_STORAGE_KEY = 'tabletLite.uiTimeZone';
  const TIME_ZONES = [
    { id: 'Asia/Dubai', label: 'Abu Dhabi', short: 'Abu Dhabi' },
    { id: 'Asia/Kolkata', label: 'IST', short: 'IST' },
    { id: 'UTC', label: 'UTC', short: 'UTC' },
  ];
  const DEFAULT_UI_TIME_ZONE = 'Asia/Dubai';

  const state = {
    connected: false,
    catalog: null,
    tasks: [],
    selectedTaskId: null,
    view: 'list', // list | detail
    uiTimeZone: loadStoredTimeZone(),
    form: {
      taskTypeId: '',
      workplaceId: '',
      materialId: '',
      allowedDestinationId: '',
      loaderEquipmentId: '',
      quantity: '',
      deadlineHours: '',
      eventDate: '',
      eventTime: '',
      expectedStartDate: '',
      estimatedStartTime: '',
      estimatedEndTime: '',
    },
    syncing: false,
    clockTimer: null,
    deviceId: '',
    disconnected: false,
  };

  /** @type {Record<string, Window|null>} */
  const openWindows = {};

  let els = {};

  function loadStoredTimeZone() {
    try {
      const raw = global.sessionStorage?.getItem(TZ_STORAGE_KEY);
      if (TIME_ZONES.some((z) => z.id === raw)) return raw;
    } catch (_) { /* ignore */ }
    return DEFAULT_UI_TIME_ZONE;
  }

  function getActiveTimeZone() {
    return state.uiTimeZone || DEFAULT_UI_TIME_ZONE;
  }

  function timeZoneMeta(id) {
    return TIME_ZONES.find((z) => z.id === (id || getActiveTimeZone()))
      || TIME_ZONES[0];
  }

  function normalizeTimeZoneId(raw) {
    const tz = String(raw || '').trim();
    if (!tz) return '';
    if (/asia\/dubai/i.test(tz)) return 'Asia/Dubai';
    if (/asia\/kolkata|asia\/calcutta/i.test(tz)) return 'Asia/Kolkata';
    if (/^utc$/i.test(tz) || /\butc\b/i.test(tz)) return 'UTC';
    return tz;
  }

  function catalogTimeZone() {
    const tz = normalizeTimeZoneId(state.catalog?.timeZone || '');
    return tz || DEFAULT_UI_TIME_ZONE;
  }

  function setUiTimeZone(id) {
    const normalized = normalizeTimeZoneId(id);
    const next = TIME_ZONES.some((z) => z.id === normalized) ? normalized : DEFAULT_UI_TIME_ZONE;
    state.uiTimeZone = next;
    try {
      global.sessionStorage?.setItem(TZ_STORAGE_KEY, next);
    } catch (_) { /* ignore */ }
    if (els.timeZone) els.timeZone.value = next;
    updateTimeZoneHints();
    updateClock();
    renderTasks();
  }

  function updateTimeZoneHints() {
    const label = timeZoneMeta().short;
    const text = `Times use ${label} (${getActiveTimeZone()}).`;
    if (els.startTimeHint) els.startTimeHint.textContent = text;
    if (els.endTimeHint) els.endTimeHint.textContent = text;
    if (els.eventTimeHint) {
      els.eventTimeHint.textContent =
        `Converted to eventTime/timestamp; used as estimated start (${label}).`;
    }
  }

  function getPartsInTimeZone(date, timeZone) {
    const normalized = normalizeTimeZoneId(timeZone) || DEFAULT_UI_TIME_ZONE;
    const fmt = new Intl.DateTimeFormat('en-US', {
      timeZone: normalized,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    });
    const parts = Object.fromEntries(fmt.formatToParts(date).map((p) => [p.type, p.value]));
    let hour = Number(parts.hour);
    if (hour === 24) hour = 0;
    return {
      year: Number(parts.year),
      month: Number(parts.month),
      day: Number(parts.day),
      hour,
      minute: Number(parts.minute),
    };
  }

  /** Resolve wall-clock Y-M-D + HH:mm in a named zone to a UTC instant. */
  function wallClockToUtcMs(dateYmd, hm, timeZone) {
    const dateMatch = String(dateYmd || '').trim().match(/^(\d{4})-(\d{2})-(\d{2})/);
    const timeMatch = String(hm || '').trim().match(/^(\d{1,2}):(\d{2})/);
    if (!dateMatch || !timeMatch) return null;
    const y = Number(dateMatch[1]);
    const mo = Number(dateMatch[2]);
    const d = Number(dateMatch[3]);
    const hh = Number(timeMatch[1]);
    const mm = Number(timeMatch[2]);
    if (hh > 23 || mm > 59) return null;

    let utc = Date.UTC(y, mo - 1, d, hh, mm, 0);
    for (let i = 0; i < 3; i++) {
      const p = getPartsInTimeZone(new Date(utc), timeZone);
      const asUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, 0);
      const desired = Date.UTC(y, mo - 1, d, hh, mm, 0);
      utc += desired - asUtc;
    }
    return utc;
  }

  function formatHmInTimeZone(utcMs, timeZone) {
    if (utcMs == null) return '';
    const p = getPartsInTimeZone(new Date(utcMs), timeZone);
    return `${String(p.hour).padStart(2, '0')}:${String(p.minute).padStart(2, '0')}`;
  }

  function formatDateInTimeZone(utcMs, timeZone) {
    if (utcMs == null) return '';
    const p = getPartsInTimeZone(new Date(utcMs), timeZone);
    return `${p.year}-${String(p.month).padStart(2, '0')}-${String(p.day).padStart(2, '0')}`;
  }

  /** Convert task wall-clock times from catalog/mine TZ into the selected UI TZ for display. */
  function displayTaskSchedule(task) {
    const date = task?.expectedStartDate || '';
    const start = task?.estimatedStartTime || '';
    const end = task?.estimatedEndTime || '';
    const fromTz = catalogTimeZone();
    const toTz = getActiveTimeZone();
    if (!date || (!start && !end) || fromTz === toTz) {
      return {
        expectedStartDate: date,
        estimatedStartTime: start ? String(start).slice(0, 5) : '',
        estimatedEndTime: end ? String(end).slice(0, 5) : '',
      };
    }
    const startMs = start ? wallClockToUtcMs(date, start, fromTz) : null;
    const endMs = end ? wallClockToUtcMs(date, end, fromTz) : null;
    return {
      expectedStartDate: startMs != null ? formatDateInTimeZone(startMs, toTz) : date,
      estimatedStartTime: startMs != null ? formatHmInTimeZone(startMs, toTz) : (start ? String(start).slice(0, 5) : ''),
      estimatedEndTime: endMs != null ? formatHmInTimeZone(endMs, toTz) : (end ? String(end).slice(0, 5) : ''),
    };
  }

  function formatClock(raw) {
    const m = String(raw || '').trim().match(/^(\d{1,2}):(\d{2})/);
    if (!m) return raw || '';
    let h = Number(m[1]);
    const min = m[2];
    if (h > 23) return raw;
    const period = h >= 12 ? 'PM' : 'AM';
    h = h % 12;
    if (h === 0) h = 12;
    return `${String(h).padStart(2, '0')}:${min} ${period}`;
  }

  function displayShiftInfo() {
    const shift = state.catalog?.shift;
    if (!shift) {
      return { label: 'Shift', startTime: '', endTime: '' };
    }

    const fromTz = catalogTimeZone();
    const toTz = getActiveTimeZone();
    const baseLabel = shift.name || 'Shift';
    const start = shift.startTime ? String(shift.startTime).slice(0, 5) : '';
    const end = shift.endTime ? String(shift.endTime).slice(0, 5) : '';
    const date = shift.mineDayDate || mineDayDate();

    if (!start || !end || fromTz === toTz) {
      return {
        label: start && end ? `${baseLabel} (${formatClock(start)} To ${formatClock(end)})` : (shift.displayLabel || baseLabel),
        startTime: start,
        endTime: end,
      };
    }

    const startMs = wallClockToUtcMs(date, start, fromTz);
    const endMs = wallClockToUtcMs(date, end, fromTz);
    const displayStart = startMs != null ? formatHmInTimeZone(startMs, toTz) : start;
    const displayEnd = endMs != null ? formatHmInTimeZone(endMs, toTz) : end;
    return {
      label: `${baseLabel} (${formatClock(displayStart)} To ${formatClock(displayEnd)})`,
      startTime: displayStart,
      endTime: displayEnd,
    };
  }

  /** Convert Ad-Hoc form wall clocks (UI TZ) to catalog/mine TZ for publish + stored card. */
  function formTimesForPublish(date, start, end) {
    const fromTz = getActiveTimeZone();
    const toTz = catalogTimeZone();
    if (!date || fromTz === toTz) {
      return {
        expectedStartDate: date,
        estimatedStartTime: start,
        estimatedEndTime: end,
      };
    }
    const startMs = start ? wallClockToUtcMs(date, start, fromTz) : null;
    const endMs = end ? wallClockToUtcMs(date, end, fromTz) : null;
    const outDate = startMs != null ? formatDateInTimeZone(startMs, toTz) : date;
    return {
      expectedStartDate: outDate,
      estimatedStartTime: startMs != null ? formatHmInTimeZone(startMs, toTz) : start,
      estimatedEndTime: endMs != null ? formatHmInTimeZone(endMs, toTz) : end,
    };
  }

  function $(id) {
    return document.getElementById(id);
  }

  function queryDeviceId() {
    try {
      return new URLSearchParams(global.location.search).get('deviceId') || '';
    } catch (_) {
      return '';
    }
  }

  function setStatus(text, isError) {
    if (!els.status) return;
    els.status.textContent = text || '';
    els.status.classList.toggle('error', !!isError);
  }

  function setOuHint() {
    if (!els.ouHint) return;
    els.ouHint.textContent =
      'Catalog uses DigiMine OU from Settings; create uses equipment OU from the backend. Keep them the same so workplaces match TMS.';
  }

  async function fetchJson(url, options) {
    const r = await fetch(url, options);
    const j = await r.json().catch(() => ({}));
    if (!r.ok) {
      const err = new Error(j.error || `Request failed (${r.status})`);
      err.payload = j;
      throw err;
    }
    return j;
  }

  async function loadCatalog() {
    const data = await fetchJson('/api/tablet/catalog');
    state.catalog = data;
    return data;
  }

  async function loadTasks() {
    const data = await fetchJson('/api/tablet/tasks');
    state.tasks = Array.isArray(data.tasks) ? data.tasks : [];
    if (data.shift && state.catalog) {
      state.catalog.shift = data.shift;
    }
    return data;
  }

  async function ensureCatalogReady({ timeoutMs = 25000 } = {}) {
    let catalog = await loadCatalog();
    if (!catalog.ouConfigured && !catalog.ouId) {
      throw new Error('DigiMine Operational unit ID is not set. Configure it in Settings, then Sync FULL.');
    }
    if (catalog.ready) {
      return catalog;
    }

    setStatus('Catalog empty — publishing Sync FULL…');
    state.syncing = true;
    try {
      if (typeof global.loadSyncPreset === 'function') {
        await global.loadSyncPreset({ type: 'FULL', publish: true, silent: true });
      } else {
        const preset = await fetchJson('/api/presets/sync?type=FULL');
        await fetchJson('/api/publish', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ topic: preset.topic, payload: preset.payloadHex || preset.json }),
        });
      }
    } catch (e) {
      state.syncing = false;
      throw e;
    }

    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      await new Promise((r) => setTimeout(r, 800));
      catalog = await loadCatalog();
      if (catalog.ready) {
        state.syncing = false;
        setStatus('Catalog ready from Sync FULL (Settings OU).');
        return catalog;
      }
      setStatus(`Waiting for CONFIG for OU ${shortOu(catalog.ouId)}… (${Math.round((Date.now() - started) / 1000)}s)`);
    }

    state.syncing = false;
    throw new Error('Timed out waiting for task types / OU workplaces after Sync FULL.');
  }

  function shortOu(ouId) {
    if (!ouId) return '(none)';
    return ouId.length > 12 ? `${ouId.slice(0, 8)}…` : ouId;
  }

  function equipmentName() {
    return state.catalog?.equipment?.name
      || state.catalog?.equipmentId
      || 'EQUIPMENT';
  }

  function isHaulingSecondary() {
    if (state.catalog?.isHaulingSecondaryEquipment === true) return true;
    const eq = state.catalog?.equipment;
    if (!eq) return false;
    const role = String(eq.operationRole || '').toLowerCase();
    if (role !== 'secondary') return false;
    const ops = eq.assignedOperations || [];
    if (!ops.length) return true; // equipmentlist omits ops; Secondary alone is enough for dialog
    return ops.some((o) => {
      const v = String(o).toLowerCase();
      return v === 'hauling' || v === '1';
    });
  }

  function typeIdEquals(a, b) {
    return String(a || '').toLowerCase() === String(b || '').toLowerCase();
  }

  function loaderEquipmentList() {
    const list = Array.isArray(state.catalog?.equipmentList) && state.catalog.equipmentList.length
      ? state.catalog.equipmentList
      : (Array.isArray(state.catalog?.loaders) ? state.catalog.loaders : []);
    const tt = selectedTaskType();
    const primaryTypes = tt?.primaryEquipmentTypes || [];
    if (!primaryTypes.length) return list;
    const allowed = new Set(primaryTypes.map((x) => String(x).toLowerCase()));
    const filtered = list.filter((e) => allowed.has(String(e.typeId || '').toLowerCase()));
    return filtered.length ? filtered : list;
  }

  function applyDocumentTitle() {
    const name = equipmentName();
    document.title = name && name !== 'EQUIPMENT' ? name : (state.deviceId ? `Tablet ${shortOu(state.deviceId)}` : 'Tablet');
  }

  function shiftLabel() {
    return displayShiftInfo().label;
  }

  function mineDayDate() {
    return getNowParts().date;
  }

  function minutesToHm(mins) {
    const x = ((mins % (24 * 60)) + (24 * 60)) % (24 * 60);
    return `${String(Math.floor(x / 60)).padStart(2, '0')}:${String(x % 60).padStart(2, '0')}`;
  }

  function getNowParts() {
    const tz = normalizeTimeZoneId(getActiveTimeZone()) || DEFAULT_UI_TIME_ZONE;
    try {
      const fmt = new Intl.DateTimeFormat('en-US', {
        timeZone: tz,
        hour: '2-digit',
        minute: '2-digit',
        hour12: true,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
      });
      const parts = Object.fromEntries(fmt.formatToParts(new Date()).map((p) => [p.type, p.value]));
      const date = `${parts.year}-${parts.month}-${parts.day}`;
      const time12 = `${parts.hour}:${parts.minute} ${parts.dayPeriod}`;
      const p24 = getPartsInTimeZone(new Date(), tz);
      const time24 = `${String(p24.hour).padStart(2, '0')}:${String(p24.minute).padStart(2, '0')}`;
      return { date, time12, time24, timeZone: tz };
    } catch (_) {
      const d = new Date();
      const pad = (n) => String(n).padStart(2, '0');
      return {
        date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`,
        time12: d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }),
        time24: `${pad(d.getHours())}:${pad(d.getMinutes())}`,
        timeZone: tz,
      };
    }
  }

  function defaultAdHocTimes() {
    const now = getNowParts();
    const [h, m] = now.time24.split(':').map(Number);
    const startMins = h * 60 + m + 2;
    const endMins = startMins + 60;
    return {
      eventDate: now.date,
      eventTime: minutesToHm(startMins),
      expectedStartDate: now.date,
      estimatedStartTime: minutesToHm(startMins),
      estimatedEndTime: minutesToHm(endMins),
    };
  }

  /** Sync start date/time from Event Date + Event Time (UI zone). */
  function applyEventToStartFields() {
    const date = state.form.eventDate || '';
    const time = toWireTime(state.form.eventTime || '');
    state.form.expectedStartDate = date;
    state.form.estimatedStartTime = time;
    if (els.expectedStartDate) els.expectedStartDate.value = date;
    if (els.startTime) els.startTime.value = time;
  }

  /** Wall-clock Event Date+Time in selected UI zone → unix ms for envelope. */
  function eventTimeMsFromForm() {
    const date = state.form.eventDate || state.form.expectedStartDate;
    const time = toWireTime(state.form.eventTime || state.form.estimatedStartTime);
    if (!date || !time) return null;
    return wallClockToUtcMs(date, time, getActiveTimeZone());
  }

  function updateClock() {
    if (!els.mineClock) return;
    const now = getNowParts();
    const tz = timeZoneMeta().short;
    const mine = state.catalog?.mineName ? ` · ${state.catalog.mineName}` : '';
    els.mineClock.textContent = `${tz}: ${now.date} · ${now.time12}${mine}`;
    if (els.ouChip) {
      els.ouChip.textContent = `OU ${shortOu(state.catalog?.ouId)}`;
      els.ouChip.title = `Settings/catalog OU: ${state.catalog?.ouId || '(none)'}`;
    }
  }

  function startClock() {
    stopClock();
    updateClock();
    state.clockTimer = setInterval(updateClock, 15000);
  }

  function stopClock() {
    if (state.clockTimer) {
      clearInterval(state.clockTimer);
      state.clockTimer = null;
    }
  }

  function filteredTaskTypes() {
    const all = state.catalog?.taskTypes || [];
    if (isHaulingSecondary()) {
      const withSecondary = all.filter((t) => (t.secondaryEquipmentTypes || []).length > 0);
      const typeId = state.catalog?.equipment?.typeId;
      if (!typeId) return withSecondary.length ? withSecondary : all;
      const matched = withSecondary.filter((t) =>
        (t.secondaryEquipmentTypes || []).some((x) => typeIdEquals(x, typeId))
      );
      if (matched.length) return matched;
      if (withSecondary.length) return withSecondary;
      return all;
    }
    const typeId = state.catalog?.equipment?.typeId;
    if (!typeId) return all;
    const filtered = all.filter((t) => {
      const types = t.primaryEquipmentTypes || [];
      return types.length === 0 || types.some((x) => typeIdEquals(x, typeId));
    });
    return filtered.length ? filtered : all;
  }

  function isDestinationWorkplace(w) {
    return DEST_TYPES.has(String(w.destinationType || '').trim().toLowerCase());
  }

  function filteredWorkplaces(taskTypeId) {
    const all = (state.catalog?.workplaces || []).filter((w) => !isDestinationWorkplace(w));
    const tt = (state.catalog?.taskTypes || []).find((t) => t.id === taskTypeId);
    if (!tt) return all;
    const allowed = tt.workplaceTypes || [];
    if (!allowed.length) return all;
    const allowedSet = new Set(allowed.map((x) => String(x).toLowerCase()));
    const filtered = all.filter((w) => allowedSet.has(String(w.workplaceType || '').toLowerCase()));
    return filtered.length ? filtered : all;
  }

  function destinationWorkplaces() {
    return (state.catalog?.workplaces || []).filter(isDestinationWorkplace);
  }

  function filteredMaterials(workplaceId) {
    const materials = state.catalog?.materials || [];
    const links = state.catalog?.materialLinks || [];
    if (!workplaceId) return materials;
    const ids = new Set(
      links.filter((l) => l.workplaceId === workplaceId).map((l) => l.materialId)
    );
    if (!ids.size) return materials;
    return materials.filter((m) => ids.has(m.id));
  }

  function selectedTaskType() {
    return (state.catalog?.taskTypes || []).find((t) => t.id === state.form.taskTypeId);
  }

  function isDestinationRequired() {
    const v = (selectedTaskType()?.destinationAllowed || '').trim().toLowerCase();
    return v === 'true' || v === '1' || v === 'yes';
  }

  function isFormValid() {
    const f = state.form;
    if (!f.taskTypeId) return false;
    if (isHaulingSecondary()) {
      return !!f.loaderEquipmentId && !!f.eventDate && !!f.eventTime;
    }
    if (!f.workplaceId || !f.materialId) return false;
    if (isDestinationRequired() && !f.allowedDestinationId) return false;
    if (!f.quantity || !(Number(f.quantity) > 0)) return false;
    if (!f.eventDate || !f.eventTime) return false;
    if (!f.expectedStartDate || !f.estimatedStartTime || !f.estimatedEndTime) return false;
    if (f.deadlineHours) {
      const d = Number(f.deadlineHours);
      if (!(d > 0) || d >= 24) return false;
    }
    return true;
  }

  function setHaulingSecondaryFormMode(enabled) {
    const hideIds = [
      'tlQuantityField', 'tlWorkplaceField', 'tlExpectedStartDateField',
      'tlPlannedEquipmentField', 'tlStartTimeField', 'tlMaterialField',
      'tlEndTimeField', 'tlDeadlineField',
    ];
    hideIds.forEach((id) => {
      const el = $(id);
      if (el) el.hidden = !!enabled;
    });
    if (els.loaderField) els.loaderField.hidden = !enabled;
    if (els.eventDateField) els.eventDateField.hidden = false;
    if (els.eventTimeField) els.eventTimeField.hidden = false;
    if (els.destinationField && enabled) els.destinationField.hidden = true;
  }

  function renderHeader() {
    if (els.equipName) els.equipName.textContent = String(equipmentName()).toUpperCase();
    if (els.shift) els.shift.textContent = shiftLabel();
    applyDocumentTitle();
    updateClock();
    setOuHint();
  }

  function showListView() {
    state.view = 'list';
    if (els.listView) els.listView.hidden = false;
    if (els.detailView) els.detailView.hidden = true;
  }

  function showDetailView(taskId) {
    state.selectedTaskId = taskId;
    state.view = 'detail';
    const task = state.tasks.find((t) => t.taskId === taskId);
    if (els.listView) els.listView.hidden = true;
    if (els.detailView) els.detailView.hidden = false;
    renderTaskDetails(task);
  }

  function renderTaskDetails(task) {
    if (!els.detailBody) return;
    if (!task) {
      els.detailBody.innerHTML = '<p class="tl-status-line error">Task not found.</p>';
      return;
    }
    if (els.detailSubtitle) {
      els.detailSubtitle.textContent = task.taskTypeName || 'Overview';
    }
    const qty = `${task.actualQuantity || 0}/${task.plannedQuantity || 0} ${task.unitOfMeasure || ''}`.trim();
    const schedule = displayTaskSchedule(task);
    const time = [schedule.estimatedStartTime, schedule.estimatedEndTime].filter(Boolean).join(' – ') || '—';
    const rows = [
      ['Task ID', task.taskReadableId || task.taskId || '—'],
      ['Task Type', task.taskTypeName || '—'],
      ['Workplace', task.workplaceName || '—'],
      ['Equipment (Primary)', task.primaryEquipmentName
        ? `${task.primaryEquipmentName} (Primary)`
        : equipmentName()],
      ['Secondary Equipment', task.secondaryEquipmentNames || '—'],
      ['Material', task.materialName || '—'],
      ['Deadline', '—'],
      ['Quantity', qty || '—'],
      ['Expected start', schedule.expectedStartDate || '—'],
      ['Estimated times', `${time} (${timeZoneMeta().short})`],
      ['Status', task.status || '—'],
      ['Ad-hoc', task.isAdHoc ? 'Yes' : 'No'],
    ];
    els.detailBody.innerHTML = rows.map(([k, v]) => `
      <div class="tl-detail-row">
        <div class="tl-detail-label">${escapeHtml(k)}</div>
        <div class="tl-detail-value">${escapeHtml(v)}</div>
      </div>`).join('');
  }

  function renderTasks() {
    if (!els.taskList || !els.empty) return;
    if (state.view === 'detail' && state.selectedTaskId) {
      const task = state.tasks.find((t) => t.taskId === state.selectedTaskId);
      renderTaskDetails(task);
    }

    const tasks = state.tasks;
    if (!tasks.length) {
      els.empty.hidden = false;
      els.taskList.hidden = true;
      els.taskList.innerHTML = '';
      return;
    }

    els.empty.hidden = true;
    els.taskList.hidden = false;
    els.taskList.innerHTML = tasks.map((t) => {
      const selected = t.taskId === state.selectedTaskId ? ' selected' : '';
      const qty = `${t.actualQuantity || 0}/${t.plannedQuantity || 0} ${t.unitOfMeasure || ''}`.trim();
      const schedule = displayTaskSchedule(t);
      const time = [schedule.estimatedStartTime, schedule.estimatedEndTime].filter(Boolean).join(' - ');
      const primaryLabel = t.primaryEquipmentName
        ? `${t.primaryEquipmentName} (Primary)`
        : '';
      return `
        <article class="tl-task-card${selected}" data-task-id="${escapeAttr(t.taskId)}">
          <div>
            <div class="tl-task-meta">
              <span><strong>${escapeHtml(t.taskTypeName || 'Task')}</strong></span>
              <span>${escapeHtml(qty)}</span>
              <span>${escapeHtml(t.workplaceName || '—')}</span>
              <span>${escapeHtml(t.materialName || '—')}</span>
            </div>
            <div class="tl-task-time">${escapeHtml(time || schedule.expectedStartDate || '')}${t.isAdHoc ? ' · Ad-hoc' : ''}${primaryLabel ? ` · ${escapeHtml(primaryLabel)}` : ''}</div>
          </div>
          <div>›</div>
        </article>`;
    }).join('');

    els.taskList.querySelectorAll('.tl-task-card').forEach((card) => {
      card.addEventListener('click', () => {
        showDetailView(card.getAttribute('data-task-id'));
        renderTasks();
      });
    });
  }

  function fillSelect(select, items, valueKey, labelKey, placeholder, selected) {
    if (!select) return;
    const opts = [`<option value="">${escapeHtml(placeholder)}</option>`]
      .concat(items.map((item) => {
        const v = item[valueKey];
        const label = item[labelKey];
        const sel = v === selected ? ' selected' : '';
        return `<option value="${escapeAttr(v)}"${sel}>${escapeHtml(label)}</option>`;
      }));
    select.innerHTML = opts.join('');
  }

  function refreshAdHocDropdowns({ resetChildren } = {}) {
    const secondary = isHaulingSecondary();
    setHaulingSecondaryFormMode(secondary);

    const taskTypes = filteredTaskTypes();
    fillSelect(els.taskType, taskTypes, 'id', 'name', 'Select Task Type', state.form.taskTypeId);
    if (secondary && !state.form.taskTypeId && taskTypes.length === 1) {
      state.form.taskTypeId = taskTypes[0].id;
      if (els.taskType) els.taskType.value = taskTypes[0].id;
    }

    if (secondary) {
      if (resetChildren === 'taskType') {
        state.form.loaderEquipmentId = '';
      }
      const loaders = loaderEquipmentList();
      if (
        state.form.loaderEquipmentId
        && !loaders.some((e) => e.id === state.form.loaderEquipmentId)
      ) {
        state.form.loaderEquipmentId = '';
      }
      fillSelect(
        els.loader,
        loaders,
        'id',
        'name',
        'Select Loader',
        state.form.loaderEquipmentId
      );
      updateCreateEnabled();
      return;
    }

    if (resetChildren === 'taskType') {
      state.form.workplaceId = '';
      state.form.materialId = '';
      state.form.allowedDestinationId = '';
    } else if (resetChildren === 'workplace') {
      state.form.materialId = '';
    }

    const workplaces = filteredWorkplaces(state.form.taskTypeId);
    fillSelect(els.workplace, workplaces, 'id', 'name', 'Select Workplace', state.form.workplaceId);

    const materials = filteredMaterials(state.form.workplaceId);
    fillSelect(els.material, materials, 'id', 'name', 'Select Material', state.form.materialId);

    const showDest = isDestinationRequired();
    if (els.destinationField) {
      els.destinationField.hidden = !showDest;
    }
    if (showDest) {
      fillSelect(
        els.destination,
        destinationWorkplaces(),
        'id',
        'name',
        'Select Allowed Destination',
        state.form.allowedDestinationId
      );
    } else {
      state.form.allowedDestinationId = '';
      if (els.destination) els.destination.innerHTML = '';
    }

    if (els.plannedEquipment) {
      els.plannedEquipment.value = equipmentName();
    }

    updateCreateEnabled();
  }

  function updateCreateEnabled() {
    if (els.createBtn) {
      els.createBtn.disabled = !isFormValid() || state.syncing || state.disconnected;
    }
    if (els.formError) {
      if (isHaulingSecondary() && !state.form.loaderEquipmentId) {
        els.formError.textContent = 'Select a Loader (primary equipment type for this task type).';
      } else if (isHaulingSecondary() && (!state.form.eventDate || !state.form.eventTime)) {
        els.formError.textContent = 'Event date and time are required.';
      } else if (isDestinationRequired() && !state.form.allowedDestinationId) {
        els.formError.textContent = 'Allowed Destination is required for this task type.';
      } else if (state.form.deadlineHours && Number(state.form.deadlineHours) >= 24) {
        els.formError.textContent = 'Deadline must be less than 24 hours.';
      } else if (!state.catalog?.ready) {
        els.formError.textContent = 'Catalog not ready — wait for Sync FULL (Settings OU workplaces).';
      } else {
        els.formError.textContent = '';
      }
    }
  }

  function openAdHoc() {
    const defaults = defaultAdHocTimes();
    const secondary = isHaulingSecondary();
    state.form = {
      taskTypeId: '',
      workplaceId: '',
      materialId: '',
      allowedDestinationId: '',
      loaderEquipmentId: '',
      quantity: '',
      deadlineHours: '',
      eventDate: defaults.eventDate,
      eventTime: defaults.eventTime,
      expectedStartDate: defaults.expectedStartDate,
      estimatedStartTime: defaults.estimatedStartTime,
      estimatedEndTime: defaults.estimatedEndTime,
    };
    if (els.quantity) els.quantity.value = '';
    if (els.deadline) els.deadline.value = '';
    if (els.eventDate) els.eventDate.value = state.form.eventDate;
    if (els.eventTime) els.eventTime.value = state.form.eventTime;
    if (els.expectedStartDate) els.expectedStartDate.value = state.form.expectedStartDate;
    if (els.startTime) els.startTime.value = state.form.estimatedStartTime;
    if (els.endTime) els.endTime.value = state.form.estimatedEndTime;
    updateTimeZoneHints();
    refreshAdHocDropdowns();
    if (els.adhocOverlay) els.adhocOverlay.classList.add('open');
    setStatus(secondary
      ? 'Secondary hauling Ad-Hoc — pick Task Type + Loader; set Event Time (becomes start).'
      : '');
  }

  function closeAdHoc() {
    if (els.adhocOverlay) els.adhocOverlay.classList.remove('open');
  }

  async function createAdHoc() {
    if (!isFormValid()) return;
    els.createBtn.disabled = true;
    setStatus('Publishing Ad-Hoc TaskCreated…');
    try {
      const secondary = isHaulingSecondary();
      const defaults = defaultAdHocTimes();
      applyEventToStartFields();
      const uiDate = state.form.eventDate || state.form.expectedStartDate || defaults.eventDate;
      const uiStart = toWireTime(state.form.eventTime || state.form.estimatedStartTime || defaults.eventTime);
      const uiEnd = secondary
        ? ''
        : toWireTime(state.form.estimatedEndTime || defaults.estimatedEndTime);
      const published = formTimesForPublish(uiDate, uiStart, uiEnd || uiStart);
      const eventTimeMs = eventTimeMsFromForm()
        ?? wallClockToUtcMs(uiDate, uiStart, getActiveTimeZone());
      const body = secondary
        ? {
          taskTypeId: state.form.taskTypeId,
          loaderEquipmentId: state.form.loaderEquipmentId,
          expectedStartDate: published.expectedStartDate,
          estimatedStartTime: published.estimatedStartTime,
          eventTimeMs: eventTimeMs ?? undefined,
          quantity: '0',
        }
        : {
          taskTypeId: state.form.taskTypeId,
          workplaceId: state.form.workplaceId,
          materialId: state.form.materialId,
          allowedDestinationId: isDestinationRequired() ? state.form.allowedDestinationId : null,
          quantity: String(state.form.quantity),
          deadlineHours: state.form.deadlineHours || null,
          expectedStartDate: published.expectedStartDate,
          estimatedStartTime: published.estimatedStartTime,
          estimatedEndTime: published.estimatedEndTime,
          eventTimeMs: eventTimeMs ?? undefined,
        };
      const result = await fetchJson('/api/tablet/adhoc-task', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      closeAdHoc();
      await loadTasks();
      state.selectedTaskId = result.taskId || result.task?.taskId || null;
      showListView();
      renderTasks();
      setStatus(`Ad-hoc task published (${result.taskId || 'ok'}).`);
    } catch (e) {
      if (els.formError) els.formError.textContent = e.message || String(e);
      setStatus(e.message || String(e), true);
    } finally {
      updateCreateEnabled();
    }
  }

  function toWireTime(raw) {
    if (!raw) return raw;
    if (/^\d{2}:\d{2}/.test(raw)) return raw.slice(0, 5);
    const d = new Date(`1970-01-01T${raw}`);
    if (!Number.isNaN(d.getTime())) {
      return d.toISOString().slice(11, 16);
    }
    return raw;
  }

  function escapeHtml(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function escapeAttr(s) {
    return escapeHtml(s).replace(/'/g, '&#39;');
  }

  function wireForm() {
    els.taskType?.addEventListener('change', () => {
      state.form.taskTypeId = els.taskType.value;
      refreshAdHocDropdowns({ resetChildren: 'taskType' });
    });
    els.loader?.addEventListener('change', () => {
      state.form.loaderEquipmentId = els.loader.value;
      updateCreateEnabled();
    });
    els.workplace?.addEventListener('change', () => {
      state.form.workplaceId = els.workplace.value;
      refreshAdHocDropdowns({ resetChildren: 'workplace' });
    });
    els.material?.addEventListener('change', () => {
      state.form.materialId = els.material.value;
      updateCreateEnabled();
    });
    els.destination?.addEventListener('change', () => {
      state.form.allowedDestinationId = els.destination.value;
      updateCreateEnabled();
    });
    els.quantity?.addEventListener('input', () => {
      state.form.quantity = els.quantity.value.trim();
      updateCreateEnabled();
    });
    els.deadline?.addEventListener('input', () => {
      state.form.deadlineHours = els.deadline.value.trim();
      updateCreateEnabled();
    });
    els.expectedStartDate?.addEventListener('change', () => {
      state.form.expectedStartDate = els.expectedStartDate.value;
      updateCreateEnabled();
    });
    els.startTime?.addEventListener('change', () => {
      state.form.estimatedStartTime = els.startTime.value;
      updateCreateEnabled();
    });
    els.endTime?.addEventListener('change', () => {
      state.form.estimatedEndTime = els.endTime.value;
      updateCreateEnabled();
    });
    els.eventDate?.addEventListener('change', () => {
      state.form.eventDate = els.eventDate.value;
      applyEventToStartFields();
      updateCreateEnabled();
    });
    els.eventTime?.addEventListener('change', () => {
      state.form.eventTime = els.eventTime.value;
      applyEventToStartFields();
      updateCreateEnabled();
    });
  }

  function bindShellControls() {
    els.adhocBtn?.addEventListener('click', () => openAdHoc());
    els.cancelBtn?.addEventListener('click', () => closeAdHoc());
    els.closeX?.addEventListener('click', () => closeAdHoc());
    els.createBtn?.addEventListener('click', () => createAdHoc());
    els.timeZone?.addEventListener('change', () => {
      setUiTimeZone(els.timeZone.value);
      if (els.adhocOverlay?.classList.contains('open')) {
        const defaults = defaultAdHocTimes();
        state.form.eventDate = defaults.eventDate;
        state.form.eventTime = defaults.eventTime;
        state.form.expectedStartDate = defaults.expectedStartDate;
        state.form.estimatedStartTime = defaults.estimatedStartTime;
        state.form.estimatedEndTime = defaults.estimatedEndTime;
        if (els.eventDate) els.eventDate.value = defaults.eventDate;
        if (els.eventTime) els.eventTime.value = defaults.eventTime;
        if (els.expectedStartDate) els.expectedStartDate.value = defaults.expectedStartDate;
        if (els.startTime) els.startTime.value = defaults.estimatedStartTime;
        if (els.endTime) els.endTime.value = defaults.estimatedEndTime;
        updateCreateEnabled();
      }
    });
    els.backBtn?.addEventListener('click', () => {
      showListView();
      renderTasks();
    });
    els.closeSim?.addEventListener('click', () => {
      if (isStandalone) {
        global.close();
      }
    });
    wireForm();
  }

  function cacheShellEls() {
    els = {
      ...els,
      equipName: $('tlEquipName'),
      shift: $('tlShiftLabel'),
      status: $('tlStatus'),
      empty: $('tlEmpty'),
      taskList: $('tlTaskList'),
      adhocBtn: $('tlAdHocBtn'),
      adhocOverlay: $('tlAdHocOverlay'),
      taskType: $('tlTaskType'),
      loader: $('tlLoader'),
      loaderField: $('tlLoaderField'),
      eventDate: $('tlEventDate'),
      eventTime: $('tlEventTime'),
      eventDateField: $('tlEventDateField'),
      eventTimeField: $('tlEventTimeField'),
      eventTimeHint: $('tlEventTimeHint'),
      workplace: $('tlWorkplace'),
      material: $('tlMaterial'),
      destination: $('tlDestination'),
      destinationField: $('tlDestinationField'),
      plannedEquipment: $('tlPlannedEquipment'),
      quantity: $('tlQuantity'),
      deadline: $('tlDeadline'),
      expectedStartDate: $('tlExpectedStartDate'),
      startTime: $('tlStartTime'),
      endTime: $('tlEndTime'),
      startTimeHint: $('tlStartTimeHint'),
      endTimeHint: $('tlEndTimeHint'),
      createBtn: $('tlCreateTask'),
      formError: $('tlFormError'),
      cancelBtn: $('tlAdHocCancel'),
      closeX: $('tlAdHocClose'),
      closeSim: $('tlCloseSim'),
      mineClock: $('tlMineClock'),
      timeZone: $('tlTimeZone'),
      ouChip: $('tlOuChip'),
      ouHint: $('tlOuHint'),
      listView: $('tlListView'),
      detailView: $('tlDetailView'),
      detailBody: $('tlDetailBody'),
      detailSubtitle: $('tlDetailSubtitle'),
      backBtn: $('tlBackToList'),
    };
    if (els.timeZone) els.timeZone.value = getActiveTimeZone();
    updateTimeZoneHints();
  }

  async function bootStandalone() {
    state.deviceId = queryDeviceId();
    cacheShellEls();
    bindShellControls();
    setOuHint();
    setStatus('Loading catalog…');

    if (global.opener && global.opener.closed) {
      state.disconnected = true;
      setStatus('Home simulator disconnected — close this window and reconnect.', true);
    }

    try {
      await ensureCatalogReady();
      await loadTasks();
      renderHeader();
      showListView();
      renderTasks();
      startClock();
      if (els.adhocBtn) els.adhocBtn.disabled = !state.catalog?.ready || state.disconnected;
      setStatus(state.catalog?.ready
        ? `Ready · Settings OU ${shortOu(state.catalog.ouId)} · ${state.catalog.workplaces?.length || 0} workplaces (create uses equipment OU)`
        : 'Catalog incomplete.');
    } catch (e) {
      setStatus(e.message || String(e), true);
      renderHeader();
      showListView();
      renderTasks();
      startClock();
    }

    global.addEventListener('message', (ev) => {
      if (!ev.data || ev.data.type !== 'tablet-lite-refresh') return;
      refreshStandalone().catch(() => {});
    });

    global.addEventListener('message', (ev) => {
      if (!ev.data || ev.data.type !== 'tablet-lite-disconnect') return;
      state.disconnected = true;
      stopClock();
      closeAdHoc();
      if (els.adhocBtn) els.adhocBtn.disabled = true;
      setStatus('MQTT disconnected on Home — this tablet is offline.', true);
    });
  }

  async function refreshStandalone() {
    if (state.disconnected) return;
    try {
      await loadCatalog();
      await loadTasks();
      renderHeader();
      renderTasks();
    } catch (_) { /* ignore */ }
  }

  function currentDeviceIdFromHome() {
    const label = document.getElementById('deviceIdLabel');
    const text = (label?.textContent || '').trim();
    if (text && text !== '—') return text;
    return '';
  }

  function openTabletWindow() {
    if (!state.connected) return;
    const deviceId = currentDeviceIdFromHome();
    if (!deviceId) {
      alert('No device connected.');
      return;
    }
    const name = `tablet-${deviceId}`;
    const url = `/tablet-lite.html?deviceId=${encodeURIComponent(deviceId)}`;
    const existing = openWindows[deviceId];
    if (existing && !existing.closed) {
      existing.focus();
      return;
    }
    const win = global.open(url, name, 'width=1280,height=800,menubar=no,toolbar=no,location=no,status=no');
    openWindows[deviceId] = win;
  }

  function closeAllTabletWindows() {
    Object.keys(openWindows).forEach((id) => {
      const w = openWindows[id];
      try {
        if (w && !w.closed) {
          w.postMessage({ type: 'tablet-lite-disconnect' }, '*');
          w.close();
        }
      } catch (_) { /* ignore */ }
      openWindows[id] = null;
    });
  }

  function broadcastRefresh() {
    Object.keys(openWindows).forEach((id) => {
      const w = openWindows[id];
      try {
        if (w && !w.closed) {
          w.postMessage({ type: 'tablet-lite-refresh' }, '*');
        }
      } catch (_) { /* ignore */ }
    });
  }

  function setOpenTabletEnabled(connected) {
    state.connected = !!connected;
    if (els.openBtn) {
      els.openBtn.disabled = !state.connected;
      els.openBtn.title = state.connected
        ? 'Open lite tablet in a separate window'
        : 'Connect MQTT first';
    }
    if (!state.connected) {
      closeAllTabletWindows();
    }
  }

  function onDisconnect() {
    setOpenTabletEnabled(false);
    fetch('/api/tablet/session', { method: 'DELETE' }).catch(() => {});
    state.catalog = null;
    state.tasks = [];
  }

  function initHome() {
    els.openBtn = $('openTablet');
    if (!els.openBtn) return;
    els.openBtn.disabled = true;
    els.openBtn.addEventListener('click', () => openTabletWindow());
  }

  function init() {
    if (isStandalone) {
      bootStandalone();
      return;
    }
    initHome();
  }

  global.TabletLite = {
    init,
    setConnected: setOpenTabletEnabled,
    onDisconnect,
    refresh: async () => {
      broadcastRefresh();
    },
    openWindow: openTabletWindow,
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})(window);
