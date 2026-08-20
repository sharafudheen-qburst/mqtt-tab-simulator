/* Lite OT tablet — standalone window (Tasks + Ad-Hoc) + Home Open Tablet bridge */
(function (global) {
  'use strict';

  const DEST_TYPES = new Set(['crusher', 'stockpile', 'wastedump', 'waste_dump', 'waste dump']);
  const isStandalone = document.body.classList.contains('tl-page');

  const state = {
    connected: false,
    catalog: null,
    tasks: [],
    selectedTaskId: null,
    view: 'list', // list | detail
    form: {
      taskTypeId: '',
      workplaceId: '',
      materialId: '',
      allowedDestinationId: '',
      quantity: '',
      deadlineHours: '',
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

  function applyDocumentTitle() {
    const name = equipmentName();
    document.title = name && name !== 'EQUIPMENT' ? name : (state.deviceId ? `Tablet ${shortOu(state.deviceId)}` : 'Tablet');
  }

  function shiftLabel() {
    return state.catalog?.shift?.displayLabel
      || state.catalog?.shift?.name
      || 'Shift';
  }

  function mineDayDate() {
    return state.catalog?.shift?.mineDayDate
      || new Date().toISOString().slice(0, 10);
  }

  function parseHmToMinutes(raw) {
    if (!raw) return null;
    const m = String(raw).trim().match(/^(\d{1,2}):(\d{2})/);
    if (!m) return null;
    const h = Number(m[1]);
    const min = Number(m[2]);
    if (h > 23 || min > 59) return null;
    return h * 60 + min;
  }

  function minutesToHm(mins) {
    const x = ((mins % (24 * 60)) + (24 * 60)) % (24 * 60);
    return `${String(Math.floor(x / 60)).padStart(2, '0')}:${String(x % 60).padStart(2, '0')}`;
  }

  function getShiftWindowMinutes() {
    const start = parseHmToMinutes(state.catalog?.shift?.startTime);
    const end = parseHmToMinutes(state.catalog?.shift?.endTime);
    if (start == null || end == null) return null;
    return { start, end };
  }

  function isWithinShiftMinutes(mins, window) {
    if (!window) return true;
    const { start, end } = window;
    if (start === end) return true;
    if (start < end) {
      return mins >= start && mins <= end;
    }
    // overnight
    return mins >= start || mins <= end;
  }

  function clampToShift(mins, window) {
    if (!window) return mins;
    const { start, end } = window;
    if (isWithinShiftMinutes(mins, window)) return mins;
    return start;
  }

  function addMinutesInsideShift(startMins, add, window) {
    let end = startMins + add;
    if (!window) return end;
    const { start, end: shiftEnd } = window;
    if (start < shiftEnd) {
      if (end > shiftEnd) end = shiftEnd;
      if (end < start) end = start;
      return end;
    }
    // overnight: allow wrap; if still outside, clamp to shiftEnd
    if (!isWithinShiftMinutes(end % (24 * 60), window)) {
      return shiftEnd;
    }
    return end % (24 * 60);
  }

  function getMineNowParts() {
    const tz = state.catalog?.timeZone;
    try {
      if (tz) {
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
        const time24Fmt = new Intl.DateTimeFormat('en-GB', {
          timeZone: tz,
          hour: '2-digit',
          minute: '2-digit',
          hour12: false,
        });
        const time24 = time24Fmt.format(new Date());
        return { date, time12, time24 };
      }
    } catch (_) { /* fall through */ }

    const d = new Date();
    const pad = (n) => String(n).padStart(2, '0');
    return {
      date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`,
      time12: d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' }),
      time24: `${pad(d.getHours())}:${pad(d.getMinutes())}`,
    };
  }

  function defaultAdHocTimes() {
    const now = getMineNowParts();
    const [h, m] = now.time24.split(':').map(Number);
    const window = getShiftWindowMinutes();
    let startMins = clampToShift(h * 60 + m + 2, window);
    let endMins = addMinutesInsideShift(startMins, 60, window);
    if (window && startMins === endMins) {
      endMins = addMinutesInsideShift(startMins, 1, window);
    }
    return {
      expectedStartDate: state.catalog?.shift?.mineDayDate || now.date,
      estimatedStartTime: minutesToHm(startMins),
      estimatedEndTime: minutesToHm(endMins),
    };
  }

  function timesInsideShift(startHm, endHm) {
    const window = getShiftWindowMinutes();
    if (!window) return true;
    const s = parseHmToMinutes(startHm);
    const e = parseHmToMinutes(endHm);
    if (s == null || e == null) return false;
    return isWithinShiftMinutes(s, window) && isWithinShiftMinutes(e, window);
  }

  function updateClock() {
    if (!els.mineClock) return;
    const now = getMineNowParts();
    const day = mineDayDate();
    const mine = state.catalog?.mineName ? ` · ${state.catalog.mineName}` : '';
    els.mineClock.textContent = `Mine day: ${day} · ${now.time12}${mine}`;
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
    const typeId = state.catalog?.equipment?.typeId;
    if (!typeId) return all;
    const filtered = all.filter((t) => {
      const types = t.primaryEquipmentTypes || [];
      return types.length === 0 || types.some((x) => String(x).toLowerCase() === String(typeId).toLowerCase());
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
    if (!f.taskTypeId || !f.workplaceId || !f.materialId) return false;
    if (isDestinationRequired() && !f.allowedDestinationId) return false;
    if (!f.quantity || !(Number(f.quantity) > 0)) return false;
    if (!f.expectedStartDate || !f.estimatedStartTime || !f.estimatedEndTime) return false;
    if (!timesInsideShift(f.estimatedStartTime, f.estimatedEndTime)) return false;
    if (f.deadlineHours) {
      const d = Number(f.deadlineHours);
      if (!(d > 0) || d >= 24) return false;
    }
    return true;
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
    const time = [task.estimatedStartTime, task.estimatedEndTime].filter(Boolean).join(' – ') || '—';
    const rows = [
      ['Task ID', task.taskReadableId || task.taskId || '—'],
      ['Task Type', task.taskTypeName || '—'],
      ['Workplace', task.workplaceName || '—'],
      ['Equipment', task.primaryEquipmentName || equipmentName()],
      ['Material', task.materialName || '—'],
      ['Deadline', '—'],
      ['Quantity', qty || '—'],
      ['Expected start', task.expectedStartDate || '—'],
      ['Estimated times', time],
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
      const time = [t.estimatedStartTime, t.estimatedEndTime].filter(Boolean).join(' - ');
      return `
        <article class="tl-task-card${selected}" data-task-id="${escapeAttr(t.taskId)}">
          <div>
            <div class="tl-task-meta">
              <span><strong>${escapeHtml(t.taskTypeName || 'Task')}</strong></span>
              <span>${escapeHtml(qty)}</span>
              <span>${escapeHtml(t.workplaceName || '—')}</span>
              <span>${escapeHtml(t.materialName || '—')}</span>
            </div>
            <div class="tl-task-time">${escapeHtml(time || t.expectedStartDate || '')}${t.isAdHoc ? ' · Ad-hoc' : ''}</div>
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
    const taskTypes = filteredTaskTypes();
    fillSelect(els.taskType, taskTypes, 'id', 'name', 'Select Task Type', state.form.taskTypeId);

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
      if (isDestinationRequired() && !state.form.allowedDestinationId) {
        els.formError.textContent = 'Allowed Destination is required for this task type.';
      } else if (state.form.deadlineHours && Number(state.form.deadlineHours) >= 24) {
        els.formError.textContent = 'Deadline must be less than 24 hours.';
      } else if (
        state.form.estimatedStartTime
        && state.form.estimatedEndTime
        && !timesInsideShift(state.form.estimatedStartTime, state.form.estimatedEndTime)
      ) {
        const shift = state.catalog?.shift;
        els.formError.textContent = `Times must be inside current shift (${shift?.startTime || '?'} – ${shift?.endTime || '?'} mine local).`;
      } else if (!state.catalog?.ready) {
        els.formError.textContent = 'Catalog not ready — wait for Sync FULL (Settings OU workplaces).';
      } else {
        els.formError.textContent = '';
      }
    }
  }

  function openAdHoc() {
    const defaults = defaultAdHocTimes();
    state.form = {
      taskTypeId: '',
      workplaceId: '',
      materialId: '',
      allowedDestinationId: '',
      quantity: '',
      deadlineHours: '',
      expectedStartDate: defaults.expectedStartDate,
      estimatedStartTime: defaults.estimatedStartTime,
      estimatedEndTime: defaults.estimatedEndTime,
    };
    if (els.quantity) els.quantity.value = '';
    if (els.deadline) els.deadline.value = '';
    if (els.expectedStartDate) els.expectedStartDate.value = state.form.expectedStartDate;
    if (els.startTime) els.startTime.value = state.form.estimatedStartTime;
    if (els.endTime) els.endTime.value = state.form.estimatedEndTime;
    refreshAdHocDropdowns();
    if (els.adhocOverlay) els.adhocOverlay.classList.add('open');
  }

  function closeAdHoc() {
    if (els.adhocOverlay) els.adhocOverlay.classList.remove('open');
  }

  async function createAdHoc() {
    if (!isFormValid()) return;
    els.createBtn.disabled = true;
    setStatus('Publishing Ad-Hoc TaskCreated…');
    try {
      const body = {
        taskTypeId: state.form.taskTypeId,
        workplaceId: state.form.workplaceId,
        materialId: state.form.materialId,
        allowedDestinationId: isDestinationRequired() ? state.form.allowedDestinationId : null,
        quantity: String(state.form.quantity),
        deadlineHours: state.form.deadlineHours || null,
        expectedStartDate: state.form.expectedStartDate,
        estimatedStartTime: toWireTime(state.form.estimatedStartTime),
        estimatedEndTime: toWireTime(state.form.estimatedEndTime),
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
  }

  function bindShellControls() {
    els.adhocBtn?.addEventListener('click', () => openAdHoc());
    els.cancelBtn?.addEventListener('click', () => closeAdHoc());
    els.closeX?.addEventListener('click', () => closeAdHoc());
    els.createBtn?.addEventListener('click', () => createAdHoc());
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
      createBtn: $('tlCreateTask'),
      formError: $('tlFormError'),
      cancelBtn: $('tlAdHocCancel'),
      closeX: $('tlAdHocClose'),
      closeSim: $('tlCloseSim'),
      mineClock: $('tlMineClock'),
      ouChip: $('tlOuChip'),
      ouHint: $('tlOuHint'),
      listView: $('tlListView'),
      detailView: $('tlDetailView'),
      detailBody: $('tlDetailBody'),
      detailSubtitle: $('tlDetailSubtitle'),
      backBtn: $('tlBackToList'),
    };
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
