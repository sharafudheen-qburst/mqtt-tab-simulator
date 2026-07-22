/**
 * Mirror browser localStorage to SQLite via /api/storage.
 * Use for page-specific keys so data survives server restarts and browser refresh.
 */
const StorageSync = {
  async load(key, { fallback = null } = {}) {
    let localValue = null;
    try {
      localValue = localStorage.getItem(key);
    } catch (err) {
      console.warn('localStorage read failed:', err);
    }

    try {
      const r = await fetch(`/api/storage/${encodeURIComponent(key)}`);
      if (r.ok) {
        const j = await r.json();
        if (j.value != null) {
          try {
            localStorage.setItem(key, j.value);
          } catch {
            // ignore quota errors
          }
          return j.value;
        }
      }
    } catch (err) {
      console.warn('Server storage read failed:', err);
    }

    return localValue ?? fallback;
  },

  async save(key, value) {
    try {
      localStorage.setItem(key, value);
    } catch (err) {
      console.warn('localStorage write failed:', err);
    }

    try {
      await fetch('/api/storage', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ key, value }),
      });
    } catch (err) {
      console.warn('Server storage write failed:', err);
    }
  },

  async remove(key) {
    try {
      localStorage.removeItem(key);
    } catch {
      // ignore
    }

    try {
      await fetch(`/api/storage/${encodeURIComponent(key)}`, { method: 'DELETE' });
    } catch (err) {
      console.warn('Server storage delete failed:', err);
    }
  },
};
