// Tiny fetch wrapper for the manager's own /api. Throws on non-2xx with the
// API error message so callers can show it in a toast.
window.api = (function () {
  async function request(method, path, body) {
    const opt = { method, headers: {}, credentials: "same-origin" };
    if (body !== undefined) {
      opt.headers["Content-Type"] = "application/json";
      opt.body = JSON.stringify(body);
    }
    let res, text;
    try {
      res = await fetch(path, opt);
    } catch (e) {
      throw new Error(t ? t("toast.network_error") : "Network error");
    }
    if (res.status === 204) return null;
    text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!res.ok) {
      const msg = (data && data.message) ? data.message : `HTTP ${res.status}`;
      const err = new Error(msg);
      err.status = res.status;
      err.code = data && data.code;
      err.data = data;
      // Authentication required -> surface the login screen globally.
      if (res.status === 401 && path !== "/api/auth/login") {
        window.app && window.app.onUnauthorized();
      }
      throw err;
    }
    return data;
  }
  return {
    get: (p) => request("GET", p),
    post: (p, body) => request("POST", p, body ?? {}),
    put: (p, body) => request("PUT", p, body ?? {}),
    del: (p) => request("DELETE", p),
  };
})();
