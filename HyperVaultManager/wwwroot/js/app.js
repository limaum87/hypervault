/* HyperVault Manager — frontend SPA. Renders views from /api, supports
   EN/PT via window.i18n. Hash-based routing. */

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const t = (k) => i18n.t(k);

/* ---------- formatting helpers ---------- */
function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function fmtBytes(n) {
  n = Number(n || 0);
  if (!n) return "—";
  const u = ["B", "KB", "MB", "GB", "TB", "PB"];
  const i = Math.min(u.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  return `${(n / Math.pow(1024, i)).toFixed(i ? 1 : 0)} ${u[i]}`;
}
// Like fmtBytes but renders 0 as "0 B" (useful for free/total disk space).
function fmtBytesFixed(n) {
  n = Number(n || 0);
  if (!n) return "0 B";
  return fmtBytes(n);
}
function fmtDate(iso) {
  if (!iso) return `<span class="muted">${t("common.never")}</span>`;
  const d = new Date(iso);
  if (isNaN(d)) return esc(iso);
  return d.toLocaleString(currentLocale(), { dateStyle: "short", timeStyle: "short" });
}
function fmtRelative(iso) {
  if (!iso) return `<span class="muted">${t("common.never")}</span>`;
  const d = new Date(iso); if (isNaN(d)) return esc(iso);
  const s = Math.round((Date.now() - d.getTime()) / 1000);
  const abs = Math.abs(s);
  const fmt = (v, u) => d.toLocaleString(currentLocale(), { dateStyle: "short", timeStyle: "short" }) + ` (${v}${u})`;
  if (abs < 60) return fmt(s, "s");
  if (abs < 3600) return fmt(Math.round(s / 60), "m");
  if (abs < 86400) return fmt(Math.round(s / 3600), "h");
  return fmt(Math.round(s / 86400), "d");
}
function fmtDuration(sec) {
  sec = Number(sec || 0); if (!sec) return "—";
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m ${s}s`;
  return `${s}s`;
}
function currentLocale() { return i18n.current === "pt" ? "pt-BR" : "en-US"; }

function statusBadge(status) {
  const key = `status.${(status || "unknown").toLowerCase()}`;
  const label = t(key);
  return `<span class="badge ${esc(status || "unknown")}"><span class="dot"></span>${esc(label)}</span>`;
}
function typeBadge(type) {
  return `<span class="badge unknown">${esc(t(`jobs.types.${type}`) === `jobs.types.${type}` ? type : t(`jobs.types.${type}`))}</span>`;
}

/* ---------- icons & reusable components ---------- */
// Renders a Lucide icon placeholder; window.lucide.createIcons() swaps it for an <svg>.
const IC = (n, size = 20, cls = "") =>
  `<i data-lucide="${n}" data-size="${size}"${cls ? ` class="${cls}"` : ""}></i>`;
const paint = (root) => { if (window.lucide) window.lucide.createIcons(root || document); };

function StatCard(label, value, icon, tone, sub) {
  return `<div class="stat-card">
    <div>
      <div class="stat-label">${esc(label)}</div>
      <div class="stat-value">${esc(value)}</div>
      ${sub ? `<div class="stat-trend">${esc(sub)}</div>` : ""}
    </div>
    <div class="stat-ico tone-${tone}">${IC(icon, 26)}</div>
  </div>`;
}
function SearchInput(id, value, placeholder) {
  return `<div class="search-input grow">${IC("search", 16)}<input id="${id}" type="text" value="${esc(value)}" placeholder="${esc(placeholder)}" /></div>`;
}
function FilterSelect(id, opts, value) {
  const optHtml = opts.map(o => `<option value="${esc(o.value)}"${String(o.value) === String(value) ? " selected" : ""}>${esc(o.label)}</option>`).join("");
  return `<div class="filter-select"><select id="${id}">${optHtml}</select>${IC("chevron-down", 15)}</div>`;
}
const HEALTH_SLOTS = 10;
function StatusHistory(vm) {
  // Real backup history (oldest -> newest) coming from the API. Left-pad with
  // empty slots so the layout stays stable and the newest run is rightmost.
  const history = Array.isArray(vm && vm.backupHistory) ? vm.backupHistory : [];
  const padded = [];
  for (let i = 0; i < HEALTH_SLOTS - history.length; i++) padded.push(null);
  history.forEach(h => padded.push(h));
  const bars = padded.map(h => {
    if (!h || !h.status) {
      return `<span class="sh-bar h-empty" title="${esc(t("vms.no_backup"))}"></span>`;
    }
    const cls = mapBackupStatusToHealth(h.status);
    const when = h.at ? fmtRelativeShort(h.at) : t("common.never");
    return `<span class="sh-bar ${cls}" title="${esc(h.status)} · ${esc(when)}"></span>`;
  });
  return `<div class="status-history" title="${esc(t("vms.health"))}">${bars.join("")}</div>`;
}
function mapBackupStatusToHealth(s) {
  if (!s) return "h-unavailable";
  if (s === "succeeded") return "h-healthy";
  if (s === "failed" || s === "canceled") return "h-error";
  if (s === "running" || s === "queued") return "h-warning";
  return "h-healthy";
}
function TagBadge(tag) {
  const style = tag.color ? ` style="background:${esc(tag.color)}"` : "";
  return `<span class="tag tag-${tag.key}"${style}>${esc(tag.label)}</span>`;
}
function ActionButton(label, onclick, opts = {}) {
  return `<button class="btn-detail" onclick="${onclick}">${opts.icon ? IC(opts.icon, 14) + " " : ""}${esc(label)}</button>`;
}

function vmTags(vm) {
  // Real tags come from the API (vm.tags). Rendered read-only in the table;
  // the "manage tags" button opens an editor modal (see manageTagsVm).
  return Array.isArray(vm && vm.tags) ? vm.tags : [];
}
function fmtRelativeShort(iso) {
  if (!iso) return t("common.never");
  const d = new Date(iso); if (isNaN(d)) return esc(iso);
  const s = Math.round((Date.now() - d.getTime()) / 1000);
  const abs = Math.abs(s);
  if (abs < 60) return s <= 0 ? "now" : `${s}s ago`;
  if (abs < 3600) return `${Math.round(s / 60)}m ago`;
  if (abs < 86400) return `${Math.round(s / 3600)}h ago`;
  if (abs < 2592000) return `${Math.round(s / 86400)}d ago`;
  return d.toLocaleDateString(currentLocale(), { dateStyle: "medium" });
}

/* ---------- toast ---------- */
let toastTimer;
function toast(msg, kind = "ok") {
  const el = $("#toast");
  el.className = `toast ${kind}`;
  el.textContent = msg;
  el.classList.remove("hidden");
  requestAnimationFrame(() => el.classList.add("show"));
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.classList.remove("show"); setTimeout(() => el.classList.add("hidden"), 250); }, 3600);
}

/* ---------- modal ---------- */
let _modalCloseHook = null; // optional cleanup invoked once when the modal closes (e.g. FLR session teardown)
function openModal(titleKey, bodyHtml, footHtml = "") {
  _modalCloseHook = null; // opening any modal clears a stale hook unless re-set by the caller
  $("#modalTitle").textContent = t(titleKey);
  const body = $("#modalBody"); body.innerHTML = bodyHtml;
  i18n.apply(body);
  const foot = $("#modalFoot"); foot.innerHTML = footHtml; i18n.apply(foot);
  $("#modalRoot").classList.remove("hidden");
  paint($("#modalRoot"));
  $("#modalRoot").setAttribute("aria-hidden", "false");
  const f = body.querySelector("input,select,textarea"); if (f) setTimeout(() => f.focus(), 30);
}
function closeModal() {
  const hook = _modalCloseHook;
  _modalCloseHook = null;
  $("#modalRoot").classList.add("hidden");
  $("#modalRoot").setAttribute("aria-hidden", "true");
  if (hook) { try { hook(); } catch (_) { /* best-effort cleanup */ } }
}
document.addEventListener("click", (e) => { if (e.target.matches("[data-close]")) closeModal(); });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeModal(); });

/* Native confirm()/alert() look rough; route confirmations through the existing
   modal system so the UI stays consistent. Returns a Promise<boolean>. If a
   modal is already open (e.g. the restore form), it is snapshotted and restored
   on cancel, so we don't lose the user's input. */
let _confirmSavedModal = null;
function confirmModal(message, opts = {}) {
  const { titleKey = "common.delete", confirmKey = "common.delete", danger = true } = opts;
  if (!$("#modalRoot").classList.contains("hidden")) {
    _confirmSavedModal = {
      title: $("#modalTitle").textContent,
      body: $("#modalBody").innerHTML,
      foot: $("#modalFoot").innerHTML
    };
  }
  return new Promise((resolve) => {
    let done = false;
    const finish = (val) => {
      if (done) return; done = true;
      if (cleanup) cleanup();
      if (_confirmSavedModal) {
        $("#modalTitle").textContent = _confirmSavedModal.title;
        $("#modalBody").innerHTML = _confirmSavedModal.body;
        $("#modalFoot").innerHTML = _confirmSavedModal.foot;
        i18n.apply($("#modalRoot"));
        _confirmSavedModal = null;
      } else {
        closeModal();
      }
      resolve(val);
    };
    const icon = danger ? IC("alert-triangle", 22) : IC("help-circle", 22);
    const body = `<div class="confirm-body">
      <div class="confirm-icon ${danger ? "danger" : "info"}">${icon}</div>
      <div class="confirm-msg">${esc(message)}</div>
    </div>`;
    const foot = `<button class="btn ghost" id="cfCancel">${t("common.cancel")}</button>
      <button class="btn ${danger ? "danger" : "primary"}" id="cfOk">${t(confirmKey)}</button>`;
    openModal(titleKey, body, foot);
    $("#cfOk").onclick = () => finish(true);
    $("#cfCancel").onclick = () => finish(false);
    // Esc/Enter and backdrop clicks must drive confirm/cancel instead of the
    // global handlers that would just hide (and drop) the underlying modal.
    const onKey = (e) => {
      if (e.key === "Escape") { e.stopImmediatePropagation(); e.preventDefault(); finish(false); }
      else if (e.key === "Enter") { e.stopImmediatePropagation(); e.preventDefault(); finish(true); }
    };
    const onBackdrop = (e) => {
      if (e.target.closest(".modal-backdrop")) { e.stopImmediatePropagation(); e.preventDefault(); finish(false); }
    };
    document.addEventListener("keydown", onKey, { capture: true });
    document.addEventListener("click", onBackdrop, { capture: true });
    const cleanup = () => {
      document.removeEventListener("keydown", onKey, { capture: true });
      document.removeEventListener("click", onBackdrop, { capture: true });
    };
  });
}

/* ---------- topbar ---------- */
function setTopbar(titleKey, actionsHtml = "") {
  $("#pageTitle").textContent = t(titleKey);
  const a = $("#topbarActions"); a.innerHTML = actionsHtml; i18n.apply(a);
}

/* ---------- shared table helpers ---------- */
function emptyRow(cols, icon, key) {
  return `<tr><td colspan="${cols}"><div class="empty">${IC(icon, 30)}<div data-i18n="${key}">${t(key)}</div></div></td></tr>`;
}
function pageLoader() {
  return `<div class="loader"><span class="spinner"></span><span data-i18n="common.loading">${t("common.loading")}</span></div>`;
}

/* ===================================================================== */
/* ROUTING                                                                */
/* ===================================================================== */
let refreshTimer = null;
function stopRefresh() { if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; } }
function autoRefresh(ms = 12000) { stopRefresh(); refreshTimer = setInterval(() => router(true), ms); }

const ROUTES = ["dashboard", "hosts", "vms", "storages", "jobs", "backups", "verifications", "restores", "settings"];

let currentUser = null; // { id, username, role }

async function router(silent = false) {
  if (!currentUser) { showLogin(); return; }
  const hash = (location.hash || "#dashboard").slice(1);
  const route = ROUTES.includes(hash) ? hash : "dashboard";
  $$(".nav-item").forEach((a) => a.classList.toggle("active", a.dataset.route === route));
  if (!silent) $("#view").innerHTML = pageLoader();
  try {
    switch (route) {
      case "dashboard": await viewDashboard(); break;
      case "hosts": await viewHosts(); break;
      case "vms": await viewVms(); break;
      case "storages": await viewStorages(); break;
      case "jobs": await viewJobs(); break;
      case "backups": await viewBackups(); break;
      case "verifications": await viewVerifications(); break;
      case "restores": await viewRestores(); break;
      case "settings": await viewSettings(); break;
    }
    autoRefresh();
    paint(document);
  } catch (err) {
    $("#view").innerHTML = `<div class="empty">${IC("alert-triangle", 30)}<div>${esc(err.message)}</div></div>`;
    paint(document);
    stopRefresh();
  }
}

/* ===================================================================== */
/* DASHBOARD                                                              */
/* ===================================================================== */
async function viewDashboard() {
  setTopbar("dashboard.title", `<button class="btn" onclick="router()">${IC("refresh-cw", 16)} <span data-i18n="common.refresh">${t("common.refresh")}</span></button>`);
  const d = await api.get("/api/dashboard");
  const kpi = (labelKey, val, cls = "", sub = "") => `
    <div class="card kpi ${cls}">
      <div class="k-label" data-i18n="${labelKey}">${t(labelKey)}</div>
      <div class="k-val">${val}</div>${sub ? `<div class="k-sub">${sub}</div>` : ""}
    </div>`;
  let html = `<div class="grid kpi-grid">
    ${kpi("dashboard.total_hosts", d.totalHosts, "", `${d.onlineHosts} ${t("status.online").toLowerCase()}`)}
    ${kpi("dashboard.online_hosts", d.onlineHosts, "ok")}
    ${kpi("dashboard.offline_hosts", d.offlineHosts, "err")}
    ${kpi("dashboard.total_vms", d.totalVms, "accent")}
    ${kpi("dashboard.vms_without_backup", d.vmsWithoutBackup, d.vmsWithoutBackup ? "err" : "ok")}
    ${kpi("dashboard.backups_24h", d.backupsLast24h)}
    ${kpi("dashboard.failed_24h", d.failedBackupsLast24h, d.failedBackupsLast24h ? "err" : "")}
    ${kpi("dashboard.estimated_storage", fmtBytes(d.estimatedStorageBytes), "accent")}
  </div>`;

  html += `<div class="section-head"><h2 data-i18n="dashboard.recent_backups">${t("dashboard.recent_backups")}</h2></div>`;
  html += backupTable(d.recentBackups, "dashboard.no_recent");

  html += `<div class="section-head"><h2 data-i18n="dashboard.recent_failures">${t("dashboard.recent_failures")}</h2></div>`;
  html += d.recentFailures.length ? backupTable(d.recentFailures, "dashboard.no_recent") : `<div class="empty">${IC("check", 30)}<div data-i18n="dashboard.no_recent">${t("dashboard.no_recent")}</div></div>`;

  $("#view").innerHTML = html;
  i18n.apply($("#view"));
}

function backupTable(rows, emptyKey) {
  if (!rows || !rows.length) return `<div class="table-wrap"><table><tbody>${emptyRow(6, "archive", emptyKey)}</tbody></table></div>`;
  return `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="backups.vm">${t("backups.vm")}</th>
      <th data-i18n="backups.host">${t("backups.host")}</th>
      <th data-i18n="backups.type">${t("backups.type")}</th>
      <th data-i18n="backups.size">${t("backups.size")}</th>
      <th data-i18n="backups.completed">${t("backups.completed")}</th>
    </tr></thead><tbody>
    ${rows.map(r => `<tr>
      <td>${statusBadge(r.status)}</td>
      <td><strong>${esc(r.vmName)}</strong></td>
      <td class="cell-dim">${esc(r.hostName)}</td>
      <td>${esc(t(`jobs.types.${r.type}`))}</td>
      <td class="cell-mono">${fmtBytes(r.sizeBytes)}</td>
      <td class="cell-mono">${fmtRelative(r.completedAt || r.startedAt || r.queuedAt)}</td>
    </tr>`).join("")}
    </tbody></table></div>`;
}

/* ===================================================================== */
/* HOSTS                                                                  */
/* ===================================================================== */
async function viewHosts() {
  setTopbar("hosts.title", `<button class="btn primary" onclick="hostForm()">${IC("plus", 16)} <span data-i18n="hosts.add">${t("hosts.add")}</span></button>`);
  const hosts = await api.get("/api/hosts");
  let html = `<div class="table-wrap"><table>
    <thead><tr>
      <th data-i18n="common.name">${t("common.name")}</th>
      <th data-i18n="hosts.address">${t("hosts.address")}</th>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="hosts.agent_version">${t("hosts.agent_version")}</th>
      <th data-i18n="hosts.vms">${t("hosts.vms")}</th>
      <th data-i18n="hosts.last_seen">${t("hosts.last_seen")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!hosts.length) html += emptyRow(7, "server", "hosts.empty");
  else html += hosts.map(h => `<tr>
    <td><strong>${esc(h.name)}</strong>${h.notes ? `<div class="cell-dim muted">${esc(h.notes)}</div>` : ""}</td>
    <td class="cell-mono">${esc(h.ipOrFqdn || "—")}<div class="cell-dim muted">:${esc(h.port)} (${h.useHttps ? "https" : "http"})</div></td>
    <td>${statusBadge(h.status)}</td>
    <td class="cell-mono">${esc(h.agentVersion || "—")}</td>
    <td class="cell-mono">${h.vmCount}</td>
    <td class="cell-mono">${fmtRelative(h.lastSeenAt)}</td>
    <td class="t-actions">
      <button class="btn sm icon-only" onclick="testHost(${h.id})" title="${esc(t("hosts.test_connection"))}">${IC("plug", 14)}</button>
      <button class="btn sm icon-only" onclick="syncHost(${h.id})" title="${esc(t("hosts.sync_vms"))}">${IC("download", 14)}</button>
      <button class="btn sm icon-only" onclick="hostForm(${h.id})" title="${esc(t("common.edit"))}">${IC("pencil", 14)}</button>
      <button class="btn sm icon-only danger" onclick="delHost(${h.id}, '${esc(h.name)}')" title="${esc(t("common.delete"))}">${IC("trash-2", 14)}</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function hostForm(id = null) {
  let h = {};
  if (id) h = await api.get(`/api/hosts/${id}`);
  const body = `<form id="hf" class="form-grid">
    <div class="field"><label data-i18n="common.name">${t("common.name")}</label><input name="name" value="${esc(h.name || "")}" required /></div>
    <div class="field"><label data-i18n="hosts.address">${t("hosts.address")}</label><input name="ipOrFqdn" value="${esc(h.ipOrFqdn || "")}" required /></div>
    <div class="field"><label data-i18n="hosts.port">${t("hosts.port")}</label><input name="port" type="number" value="${h.port ?? 5443}" required /></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="useHttps" ${h.useHttps !== false ? "checked" : ""}/> <span data-i18n="hosts.use_https">${t("hosts.use_https")}</span></label></div>
    <div class="field full"><label data-i18n="hosts.api_token">${t("hosts.api_token")}</label>
      <input name="apiToken" type="password" placeholder="${id ? "••••••••" : ""}" autocomplete="off" />
      <span class="hint" data-i18n="hosts.api_token_hint">${t("hosts.api_token_hint")}</span></div>
    <div class="field full"><label data-i18n="hosts.fingerprint">${t("hosts.fingerprint")}</label><input name="certificateFingerprint" value="${esc(h.certificateFingerprint || "")}" /></div>
    <div class="field full"><label data-i18n="hosts.notes">${t("hosts.notes")}</label><textarea name="notes">${esc(h.notes || "")}</textarea></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveHost(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal(id ? "hosts.edit" : "hosts.new", body, foot);
}

async function saveHost(id) {
  const f = $("#hf"); const fd = new FormData(f);
  const payload = {
    name: fd.get("name"), ipOrFqdn: fd.get("ipOrFqdn"),
    port: Number(fd.get("port")), useHttps: f.useHttps.checked,
    apiToken: fd.get("apiToken") || undefined,
    certificateFingerprint: fd.get("certificateFingerprint") || null,
    notes: fd.get("notes") || null
  };
  try {
    if (id) {
      await api.put(`/api/hosts/${id}`, payload); toast(t("toast.success"), "ok");
    } else {
      // Backend probes the connection first; only persists + syncs VMs if reachable.
      toast(t("toast.testing"), "info");
      const created = await api.post("/api/hosts", payload);
      toast(`${t("toast.created")} — ${t("status.online")} (${created.vmCount} VMs)`, "ok");
    }
    closeModal(); router();
  } catch (e) { toast(e.message, "err"); }
}

async function delHost(id, name) {
  if (!await confirmModal(`${t("common.confirm_delete")}\n${name}`, { titleKey: "common.delete", confirmKey: "common.delete" })) return;
  try { await api.del(`/api/hosts/${id}`); toast(t("toast.deleted"), "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

async function testHost(id) {
  toast(t("toast.testing"), "info");
  try { const r = await api.post(`/api/hosts/${id}/test`); toast(`${t("status." + r.status)}${r.error ? " — " + r.error : ""}`, r.status === "online" ? "ok" : "err"); router(); }
  catch (e) { toast(e.message, "err"); }
}

async function syncHost(id) {
  toast(t("toast.syncing"), "info");
  try { const vms = await api.post(`/api/hosts/${id}/sync-vms`); toast(`${t("toast.success")} (${vms.length} VMs)`, "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* MACHINES (Virtual Machines)                                            */
/* ===================================================================== */
let vmStore = null;          // { hosts, vms }
let vmState = { q: "", sort: "name", status: "all", host: "" };

async function viewVms() {
  setTopbar("vms.title", `<button class="btn" onclick="refreshVms()">${IC("refresh-cw", 16)} <span data-i18n="common.refresh">${t("common.refresh")}</span></button>`);
  await refreshVms();
}

async function refreshVms() {
  const [hosts, vms] = await Promise.all([api.get("/api/hosts"), api.get("/api/vms")]);
  vmStore = { hosts, vms };
  $("#view").innerHTML = vmPageHtml();
  wireVmToolbar();
  renderVmTable();
  i18n.apply($("#view"));
}

function vmCounts() {
  const vms = vmStore ? vmStore.vms : [];
  const online = vms.filter(v => /running|on/i.test(v.state || "")).length;
  const linked = vms.filter(v => !!v.lastBackupAt).length;
  return { total: vms.length, linked, online, offline: vms.length - online };
}

function vmCardsHtml() {
  const c = vmCounts();
  return [
    StatCard(t("vms.total"), c.total, "monitor", "gray", `${c.total} ${t("nav.vms").toLowerCase()}`),
    StatCard(t("vms.linked"), c.linked, "link", "blue", t("vms.linked_sub")),
    StatCard(t("status.online"), c.online, "activity", "green", t("vms.online_sub")),
    StatCard(t("status.offline"), c.offline, "monitor", "mute", t("vms.offline_sub"))
  ].join("");
}

function vmPageHtml() {
  const hostOpts = [{ value: "", label: `${t("common.host")}: ${t("common.all")}` }]
    .concat((vmStore.hosts || []).map(h => ({ value: String(h.id), label: `${t("common.host")}: ${h.name}` })));
  return `
  <div class="stat-grid" id="vmCards">${vmCardsHtml()}</div>
  <div class="toolbar" id="vmToolbar">
    ${SearchInput("vmSearch", vmState.q, t("vms.search_placeholder"))}
    <div class="result-count" id="vmCount"></div>
    ${FilterSelect("vmSort", [
      { value: "name", label: `${t("common.sort")}: ${t("common.name")}` },
      { value: "host", label: `${t("common.sort")}: ${t("common.host")}` },
      { value: "disk", label: `${t("common.sort")}: ${t("vms.disk")}` },
      { value: "backup", label: `${t("common.sort")}: ${t("vms.last_backup")}` }
    ], vmState.sort)}
    ${FilterSelect("vmStatus", [
      { value: "all", label: `${t("common.status")}: ${t("common.all")}` },
      { value: "online", label: `${t("common.status")}: ${t("status.online")}` },
      { value: "offline", label: `${t("common.status")}: ${t("status.offline")}` },
      { value: "linked", label: `${t("common.status")}: ${t("vms.linked")}` }
    ], vmState.status)}
    ${FilterSelect("vmHost", hostOpts, vmState.host)}
  </div>
  <div class="table-wrap scroll-x">
    <table id="vmTable">
      <thead><tr>
        <th data-i18n="vms.machine_id">${t("vms.machine_id")}</th>
        <th data-i18n="vms.host">${t("vms.host")}</th>
        <th data-i18n="vms.health">${t("vms.health")}</th>
        <th data-i18n="vms.state">${t("vms.state")}</th>
        <th data-i18n="vms.disk">${t("vms.disk")}</th>
        <th data-i18n="vms.tags">${t("vms.tags")}</th>
        <th data-i18n="vms.last_backup">${t("vms.last_backup")}</th>
        <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
      </tr></thead>
      <tbody id="vmTableBody"></tbody>
    </table>
  </div>`;
}

function wireVmToolbar() {
  const s = $("#vmSearch");
  if (s) s.addEventListener("input", () => { vmState.q = s.value.trim().toLowerCase(); renderVmTable(); });
  const binds = { vmSort: "sort", vmStatus: "status", vmHost: "host" };
  Object.keys(binds).forEach(id => {
    const el = $("#" + id); if (!el) return;
    el.addEventListener("change", () => { vmState[binds[id]] = el.value; renderVmTable(); });
  });
}

function vmFiltered() {
  let list = (vmStore.vms || []).slice();
  const q = vmState.q;
  if (q) list = list.filter(v => (v.name + " " + (v.externalId || "")).toLowerCase().includes(q));
  if (vmState.host) list = list.filter(v => String(v.hostId) === vmState.host);
  if (vmState.status === "online") list = list.filter(v => /running|on/i.test(v.state || ""));
  if (vmState.status === "offline") list = list.filter(v => !/running|on/i.test(v.state || ""));
  if (vmState.status === "linked") list = list.filter(v => !!v.lastBackupAt);
  list.sort((a, b) => {
    if (vmState.sort === "name") return (a.name || "").localeCompare(b.name || "");
    if (vmState.sort === "host") return (a.hostName || "").localeCompare(b.hostName || "");
    if (vmState.sort === "disk") return (a.diskSizeBytes || 0) - (b.diskSizeBytes || 0);
    if (vmState.sort === "backup") return new Date(a.lastBackupAt || 0) - new Date(b.lastBackupAt || 0);
    return 0;
  });
  return list;
}

function renderVmTable() {
  const body = $("#vmTableBody"); if (!body || !vmStore) return;
  const list = vmFiltered();
  const count = $("#vmCount");
  if (count) count.innerHTML = `<b>${list.length}</b> / ${vmStore.vms.length}`;
  if (!list.length) {
    body.innerHTML = emptyRow(8, "monitor", "vms.empty");
    i18n.apply($("#vmTable")); paint($("#vmTable"));
    return;
  }
  body.innerHTML = list.map(v => {
    const on = /running|on/i.test(v.state || "");
    const tags = vmTags(v);
    const lastBackup = v.lastBackupAt
      ? `<span class="last-online"><span class="lo-dot"></span><span class="lo-time">${fmtRelativeShort(v.lastBackupAt)}</span></span>`
      : `<span class="muted">${t("vms.no_backup")}</span>`;
    return `<tr>
      <td class="cell-id"><strong>${esc(v.name)}</strong><span class="sub-id">${esc(v.externalId || "—")}</span></td>
      <td class="cell-dim">${esc(v.hostName)}</td>
      <td>${StatusHistory(v)}</td>
      <td><span class="state-pill ${on ? "on" : "off"}">${esc(v.state || "—")}</span></td>
      <td class="cell-disk">${fmtBytes(v.diskSizeBytes)}</td>
      <td><div class="tag-cell">
        <span class="tag-list">${tags.length ? tags.map(TagBadge).join("") : `<span class="muted small">${esc(t("vms.no_tags"))}</span>`}</span>
        <button class="icon-btn tag-manage-btn" title="${esc(t("vms.manage_tags"))}" onclick="manageTagsVm(${v.id})">${IC("pencil", 14)}</button>
      </div></td>
      <td>${lastBackup}</td>
      <td>${vmActions(v)}</td>
    </tr>`;
  }).join("");
  paint($("#vmTable"));
}

function vmActions(v) {
  return `<div class="t-actions">
    ${ActionButton(t("vms.backup_now"), `backupVm(${v.hostId},${v.id},'${esc(v.name)}')`, { icon: "archive" })}
    <button class="icon-btn" title="${esc(t("common.more"))}">${IC("more-horizontal", 16)}</button>
  </div>`;
}

/* ---------- tag editor (modal) ---------- */
let _tagEdit = null; // { vmId, vmName, catalog:[{id,key,label,color}], selected:Set<int> }

async function manageTagsVm(vmId) {
  const vm = ((vmStore && vmStore.vms) || []).find(v => v.id === vmId);
  if (!vm) return;
  let catalog;
  try { catalog = await api.get("/api/tags"); }
  catch (e) { toast(e.message, "err"); return; }
  _tagEdit = {
    vmId,
    vmName: vm.name || "",
    catalog,
    selected: new Set((vm.tags || []).map(tg => tg.id))
  };
  openModal("vms.manage_tags", tagEditorBodyHtml(), tagEditorFootHtml());
  i18n.apply($("#modalRoot"));
  paint($("#modalRoot"));
  renderTagChips();
  const inp = $("#tagNewLabel"); if (inp) inp.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); addTagFromEditor(); } });
}

function tagEditorBodyHtml() {
  return `
  <div class="tag-editor">
    <div class="muted small tag-editor-vm">${esc(_tagEdit.vmName)}</div>
    <div class="tag-chips" id="tagChips"></div>
    <div class="tag-new">
      <input id="tagNewLabel" type="text" placeholder="${esc(t("vms.new_tag_placeholder"))}" />
      <button class="btn" onclick="addTagFromEditor()">${IC("plus", 14)} <span data-i18n="common.add">${t("common.add")}</span></button>
    </div>
    <div class="muted small tag-hint">${esc(t("vms.tags_hint"))}</div>
  </div>`;
}

function tagEditorFootHtml() {
  return `
  <button class="btn ghost" data-close>${IC("x", 14)} <span data-i18n="common.cancel">${t("common.cancel")}</span></button>
  <button class="btn primary" onclick="saveTagsEditor()"><span data-i18n="common.save">${t("common.save")}</span></button>`;
}

function renderTagChips() {
  const el = $("#tagChips"); if (!el || !_tagEdit) return;
  if (!_tagEdit.catalog.length) {
    el.innerHTML = `<span class="muted small">${esc(t("vms.tags_empty_catalog"))}</span>`;
    return;
  }
  el.innerHTML = _tagEdit.catalog.map(tg => {
    const on = _tagEdit.selected.has(tg.id);
    const style = (on && tg.color) ? ` style="background:${esc(tg.color)};border-color:${esc(tg.color)}"` : "";
    return `<button type="button" class="tag-chip${on ? " on" : ""}"${style} onclick="toggleTagChip(${tg.id})">${esc(tg.label)}${on ? ` ${IC("check", 12)}` : ""}</button>`;
  }).join("");
}

function toggleTagChip(id) {
  if (!_tagEdit) return;
  if (_tagEdit.selected.has(id)) _tagEdit.selected.delete(id);
  else _tagEdit.selected.add(id);
  renderTagChips();
}

async function addTagFromEditor() {
  if (!_tagEdit) return;
  const inp = $("#tagNewLabel");
  const label = (inp && inp.value || "").trim();
  if (!label) return;
  const key = label.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || ("tag" + Date.now());
  try {
    const created = await api.post("/api/tags", { key, label });
    _tagEdit.catalog.push(created);
    _tagEdit.selected.add(created.id);
    if (inp) inp.value = "";
    renderTagChips();
  } catch (e) { toast(e.message, "err"); }
}

async function saveTagsEditor() {
  if (!_tagEdit) return;
  try {
    await api.put(`/api/vms/${_tagEdit.vmId}/tags`, { tagIds: Array.from(_tagEdit.selected) });
    closeModal();
    toast(t("toast.success"), "ok");
    await refreshVms();
  } catch (e) { toast(e.message, "err"); }
}

async function backupVm(hostId, vmId, vmName) {
  const storages = await api.get("/api/storages");
  if (!storages.length) { toast("No storages configured", "err"); location.hash = "#storages"; return; }
  const sOpts = storages.map(s => `<option value="${s.id}">${esc(s.name)} — ${esc(s.path)}</option>`).join("");
  const body = `<form id="bf" class="form-grid">
    <div class="field full"><label data-i18n="vms.backup_now">${t("vms.backup_now")}</label>
      <div class="cell-dim">${esc(vmName)}</div></div>
    <div class="field"><label data-i18n="jobs.storage">${t("jobs.storage")}</label><select name="storageId" required>${sOpts}</select></div>
    <div class="field"><label data-i18n="jobs.type">${t("jobs.type")}</label><select name="type">
      <option value="full">${t("jobs.types.full")}</option>
      <option value="incremental">${t("jobs.types.incremental")}</option></select></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="runBackupVm(${hostId},${vmId})"><span data-i18n="vms.backup_now">${t("vms.backup_now")}</span></button>`;
  openModal("vms.backup_now", body, foot);
}

async function runBackupVm(hostId, vmId) {
  const f = $("#bf"); const fd = new FormData(f);
  try {
    await api.post(`/api/hosts/${hostId}/vms/${vmId}/backup`, { storageId: Number(fd.get("storageId")), type: fd.get("type") });
    toast(t("toast.queued"), "ok"); closeModal(); location.hash = "#backups"; router();
  } catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* STORAGES                                                               */
/* ===================================================================== */
async function viewStorages() {
  setTopbar("storages.title", `<button class="btn primary" onclick="storageForm()">${IC("plus", 16)} <span data-i18n="storages.add">${t("storages.add")}</span></button>`);
  const list = await api.get("/api/storages");
  let html = `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.name">${t("common.name")}</th>
      <th data-i18n="storages.type">${t("storages.type")}</th>
      <th data-i18n="storages.path">${t("storages.path")}</th>
      <th data-i18n="storages.space">${t("storages.space")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!list.length) html += emptyRow(5, "database", "storages.empty");
  else html += list.map(s => `<tr>
    <td><strong>${esc(s.name)}</strong></td>
    <td>${esc(t(`storages.types.${s.type}`))}</td>
    <td class="cell-mono">${esc(s.path)}</td>
    <td class="cell-space" id="space-${s.id}"><span class="muted">${t("common.loading") || "…"}</span></td>
    <td class="t-actions">
      <button class="btn sm icon-only" onclick="storageForm(${s.id})" title="${esc(t("common.edit"))}">${IC("pencil", 14)}</button>
      <button class="btn sm icon-only danger" onclick="delStorage(${s.id},'${esc(s.name)}')" title="${esc(t("common.delete"))}">${IC("trash-2", 14)}</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
  if (list.length) loadStorageStats(list.map(s => s.id));
}

// Fetch disk capacity (total/free) for each vault in parallel and fill the cells.
async function loadStorageStats(ids) {
  let stats;
  try { stats = await api.get("/api/storages/stats"); }
  catch (e) {
    ids.forEach(id => fillStorageSpace(id, null, e.message));
    return;
  }
  ids.forEach(id => fillStorageSpace(id, stats[id] || stats[String(id)] || null));
}

function fillStorageSpace(id, st, errMsg) {
  const cell = document.getElementById(`space-${id}`);
  if (!cell) return;
  if (!st || !st.totalBytes) {
    cell.innerHTML = `<span class="muted">${esc(t("storages.space_unavailable"))}</span>`;
    if (errMsg) cell.title = esc(errMsg);
    return;
  }
  cell.innerHTML = storageSpaceCell(st.totalBytes, st.freeBytes, st.sourceHostName);
}

function storageSpaceCell(total, free, sourceHost) {
  const used = Math.max(0, total - free);
  const pct = total > 0 ? Math.min(100, Math.round((used / total) * 100)) : 0;
  const cls = pct >= 90 ? "danger" : pct >= 75 ? "warn" : "ok";
  return `<div class="space-cell">
    <div class="space-top"><strong class="cell-mono">${fmtBytesFixed(free)}</strong>
      <span class="muted">/ ${fmtBytesFixed(total)}</span></div>
    <div class="space-track"><div class="space-fill ${cls}" style="width:${pct}%"></div></div>
    <div class="space-foot"><span class="muted">${pct}% ${esc(t("storages.used"))}</span>${sourceHost ? `<span class="muted" title="${esc(sourceHost)}">${esc(sourceHost)}</span>` : ""}</div>
  </div>`;
}

let _storageFormState = null; // { hadPassword } for the password-"keep" UX

async function storageForm(id = null) {
  const [list, hosts] = await Promise.all([
    id ? api.get("/api/storages") : Promise.resolve([]),
    api.get("/api/hosts")
  ]);
  const s = id ? (list.find(x => x.id === id) || {}) : {};
  _storageFormState = { hadPassword: !!s.hasSmbPassword };
  const isSmb = (s.type || "local_path") === "smb";
  const hostOpts = (hosts && hosts.length)
    ? hosts.map(h => `<option value="${h.id}">${esc(h.name)}</option>`).join("")
    : `<option value="">${esc(t("storages.no_hosts"))}</option>`;
  const body = `<form id="sf" class="form-grid">
    <div class="field"><label data-i18n="common.name">${t("common.name")}</label><input name="name" value="${esc(s.name || "")}" required /></div>
    <div class="field"><label data-i18n="storages.type">${t("storages.type")}</label><select name="type" id="sfType" onchange="toggleSmbFields()">
      <option value="local_path" ${!isSmb ? "selected" : ""}>${t("storages.types.local_path")}</option>
      <option value="smb" ${isSmb ? "selected" : ""}>${t("storages.types.smb")}</option></select></div>
    <div class="field full"><label data-i18n="storages.path">${t("storages.path")}</label><input name="path" id="sfPath" value="${esc(s.path || "")}" placeholder="\\\\server\\share\\backups" required /></div>

    <div class="field full smb-only" id="smbFields" style="display:${isSmb ? "" : "none"}">
      <div class="smb-grid">
        <div class="field"><label data-i18n="storages.smb_user">${t("storages.smb_user")}</label><input name="smbUsername" value="${esc(s.smbUsername || "")}" autocomplete="off" /></div>
        <div class="field"><label data-i18n="storages.smb_pass">${t("storages.smb_pass")}</label><input name="smbPassword" type="password" placeholder="${s.hasSmbPassword ? esc(t("storages.smb_pass_keep")) : ""}" autocomplete="new-password" /></div>
        <div class="field"><label data-i18n="storages.smb_domain">${t("storages.smb_domain")}</label><input name="smbDomain" value="${esc(s.smbDomain || "")}" autocomplete="off" /></div>
      </div>
      <div class="muted small">${esc(t("storages.smb_hint"))}</div>
    </div>

    <div class="field full">
      <label data-i18n="storages.test">${t("storages.test")}</label>
      <div class="smb-test-row">
        <select name="testHost" id="sfTestHost">${hostOpts}</select>
        <button type="button" class="btn" onclick="testStorage(this)">${IC("plug", 14)} <span data-i18n="storages.test_btn">${t("storages.test_btn")}</span></button>
        <span id="sfTestResult" class="muted small"></span>
      </div>
    </div>

    <div class="field full"><label data-i18n="storages.notes">${t("storages.notes")}</label><textarea name="notes">${esc(s.notes || "")}</textarea></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveStorage(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal("storages.add", body, foot);
  i18n.apply($("#modalRoot"));
}

function toggleSmbFields() {
  const el = $("#smbFields"); if (!el) return;
  el.style.display = $("#sfType") && $("#sfType").value === "smb" ? "" : "none";
}

async function testStorage(btn) {
  const f = $("#sf"); const fd = new FormData(f);
  const hostId = fd.get("testHost");
  const path = (fd.get("path") || "").trim();
  const resEl = $("#sfTestResult");
  if (!hostId) { toast(t("storages.no_hosts"), "err"); return; }
  if (!path) { toast(t("storages.path_required"), "err"); return; }
  const isSmb = fd.get("type") === "smb";
  const payload = {
    hostId: Number(hostId), path,
    smbUsername: isSmb ? (fd.get("smbUsername") || null) : null,
    smbPassword: isSmb ? (fd.get("smbPassword") || null) : null,
    smbDomain: isSmb ? (fd.get("smbDomain") || null) : null
  };
  if (resEl) { resEl.textContent = t("storages.testing"); resEl.style.color = ""; }
  if (btn) btn.disabled = true;
  try {
    const r = await api.post("/api/storages/test", payload);
    if (r && r.ok) {
      const free = (r.freeBytes != null && r.freeBytes > 0) ? ` (${fmtBytes(r.freeBytes)} ${t("storages.free")})` : "";
      const msg = t("storages.test_ok") + free;
      if (resEl) { resEl.textContent = msg; resEl.style.color = "var(--ok)"; }
      toast(msg, "ok");
    } else {
      const msg = (r && r.message) ? r.message : t("storages.test_fail");
      if (resEl) { resEl.textContent = msg; resEl.style.color = "var(--err)"; }
      toast(msg, "err");
    }
  } catch (e) {
    if (resEl) { resEl.textContent = e.message; resEl.style.color = "var(--err)"; }
    toast(e.message, "err");
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function saveStorage(id) {
  const f = $("#sf"); const fd = new FormData(f);
  const isSmb = fd.get("type") === "smb";
  const typedPass = fd.get("smbPassword");
  // On edit, blank password keeps the existing one; on create, blank = no password.
  let smbPassword;
  if (!isSmb) smbPassword = null;
  else if (id && _storageFormState && _storageFormState.hadPassword && !typedPass) smbPassword = null;
  else smbPassword = typedPass || null;
  const payload = {
    name: fd.get("name"), type: fd.get("type"), path: fd.get("path"), notes: fd.get("notes") || null,
    smbUsername: isSmb ? (fd.get("smbUsername") || null) : null,
    smbPassword, smbDomain: isSmb ? (fd.get("smbDomain") || null) : null
  };
  try {
    if (id) { await api.put(`/api/storages/${id}`, payload); toast(t("toast.success"), "ok"); }
    else { await api.post("/api/storages", payload); toast(t("toast.created"), "ok"); }
    closeModal(); router();
  } catch (e) { toast(e.message, "err"); }
}

async function delStorage(id, name) {
  if (!await confirmModal(`${t("common.confirm_delete")}\n${name}`, { titleKey: "common.delete", confirmKey: "common.delete" })) return;
  try { await api.del(`/api/storages/${id}`); toast(t("toast.deleted"), "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* JOBS                                                                   */
/* ===================================================================== */
async function viewJobs() {
  setTopbar("jobs.title", `<button class="btn primary" onclick="jobForm()">${IC("plus", 16)} <span data-i18n="jobs.add">${t("jobs.add")}</span></button>`);
  const [jobs, hosts, vms, storages] = await Promise.all([
    api.get("/api/jobs"), api.get("/api/hosts"), api.get("/api/vms"), api.get("/api/storages")]);
  const vmById = Object.fromEntries(vms.map(v => [v.id, v]));
  let html = `<div class="table-wrap"><table>
    <thead><tr>
      <th data-i18n="jobs.name">${t("jobs.name")}</th>
      <th data-i18n="jobs.vm">${t("jobs.vm")}</th>
      <th data-i18n="jobs.type">${t("jobs.type")}</th>
      <th data-i18n="jobs.schedule">${t("jobs.schedule")}</th>
      <th data-i18n="jobs.next_run">${t("jobs.next_run")}</th>
      <th data-i18n="jobs.last_run">${t("jobs.last_run")}</th>
      <th data-i18n="common.enabled">${t("common.enabled")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!jobs.length) html += emptyRow(8, "clock", "jobs.empty");
  else html += jobs.map(j => `<tr>
    <td><strong>${esc(j.name)}</strong><div class="cell-mono muted">${esc(j.vmName)} @ ${esc(j.hostName)}</div></td>
    <td class="cell-dim">${esc(j.storageName)}</td>
    <td>${esc(t(`jobs.types.${j.type}`))}</td>
    <td><span class="schedule-chip">${esc(j.scheduleLabel || t("jobs.manual_only"))}</span>${j.scheduleType === "weekly" && j.scheduleWeekdays ? `<div class="cell-mono muted">${esc(weekdaysLabel(j.scheduleWeekdays))}</div>` : ""}</td>
    <td class="cell-mono">${(j.scheduleType === "manual" || !j.enabled) ? `<span class="muted">—</span>` : fmtInTz(j.nextRunAt, j.timeZone)}</td>
    <td class="cell-mono">${fmtRelative(j.lastRunAt)}</td>
    <td>${j.enabled ? `<span class="badge online"><span class="dot"></span>on</span>` : `<span class="badge offline"><span class="dot"></span>off</span>`}</td>
    <td class="t-actions">
      <button class="btn sm icon-only primary" onclick="runJob(${j.id})" title="${esc(t("common.run_now"))}">${IC("zap", 14)}</button>
      <button class="btn sm icon-only" onclick="jobForm(${j.id})" title="${esc(t("common.edit"))}">${IC("pencil", 14)}</button>
      <button class="btn sm icon-only danger" onclick="delJob(${j.id},'${esc(j.name)}')" title="${esc(t("common.delete"))}">${IC("trash-2", 14)}</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function jobForm(id = null) {
  const [hosts, vms, storages] = await Promise.all([api.get("/api/hosts"), api.get("/api/vms"), api.get("/api/storages")]);
  let j = {};
  if (id) j = await api.get("/api/jobs").then(l => l.find(x => x.id === id));
  const detectedTz = detectTimeZone();
  const tz = j.timeZone || detectedTz;
  const tzOpts = commonTimeZones(detectedTz);
  const st = (j.scheduleType || "manual");
  const weekdays = (j.scheduleWeekdays || "").split(",").filter(Boolean).map(Number);
  const dayOpts = Array.from({ length: 31 }, (_, i) => `<option value="${i + 1}" ${j.scheduleDayOfMonth === (i + 1) ? "selected" : ""}>${i + 1}</option>`).join("");
  const wdNames = [t("days.sun"), t("days.mon"), t("days.tue"), t("days.wed"), t("days.thu"), t("days.fri"), t("days.sat")];
  const body = `<form id="jf" class="form-grid">
    <div class="field full"><label data-i18n="jobs.name">${t("jobs.name")}</label><input name="name" value="${esc(j.name || "")}" required /></div>
    <div class="field"><label data-i18n="jobs.host">${t("jobs.host")}</label><select name="hostId" id="jfHost" onchange="filterVmOptions()" required>
      ${hosts.map(h => `<option value="${h.id}" ${j.hostId === h.id ? "selected" : ""}>${esc(h.name)}</option>`).join("")}</select></div>
    <div class="field"><label data-i18n="jobs.vm">${t("jobs.vm")}</label><select name="vmId" id="jfVm" required>
      ${vms.map(v => `<option value="${v.id}" data-host="${v.hostId}" ${j.vmId === v.id ? "selected" : ""}>${esc(v.name)} (${esc(v.hostName)})</option>`).join("")}</select></div>
    <div class="field"><label data-i18n="jobs.storage">${t("jobs.storage")}</label><select name="storageId" required>
      ${storages.map(s => `<option value="${s.id}" ${j.storageId === s.id ? "selected" : ""}>${esc(s.name)}</option>`).join("")}</select></div>
    <div class="field"><label data-i18n="jobs.type">${t("jobs.type")}</label><select name="type">
      <option value="full" ${j.type === "full" ? "selected" : ""}>${t("jobs.types.full")}</option>
      <option value="incremental" ${j.type === "incremental" ? "selected" : ""}>${t("jobs.types.incremental")}</option></select></div>
    <div class="field full divider"></div>
    <div class="field full"><label data-i18n="jobs.schedule">${t("jobs.schedule")}</label><select name="scheduleType" id="jfScheduleType" onchange="toggleScheduleFields()">
      <option value="manual" ${st === "manual" ? "selected" : ""}>${t("jobs.schedule.manual")}</option>
      <option value="daily" ${st === "daily" ? "selected" : ""}>${t("jobs.schedule.daily")}</option>
      <option value="weekly" ${st === "weekly" ? "selected" : ""}>${t("jobs.schedule.weekly")}</option>
      <option value="monthly" ${st === "monthly" ? "selected" : ""}>${t("jobs.schedule.monthly")}</option>
    </select>
      <span class="hint" id="jfNextPreview" style="margin-top:6px"></span></div>
    <div class="field jfSched jfTime"><label data-i18n="jobs.schedule.time">${t("jobs.schedule.time")}</label><input name="scheduleTime" type="time" value="${esc(j.scheduleTime || "12:00")}" required onchange="updateNextPreview()" /></div>
    <div class="field jfSched jfTz"><label data-i18n="jobs.schedule.timezone">${t("jobs.schedule.timezone")}</label><select name="timeZone" onchange="updateNextPreview()">${tzOpts.map(o => `<option value="${esc(o.id)}" ${tz === o.id ? "selected" : ""}>${esc(o.label)}</option>`).join("")}</select></div>
    <div class="field full jfSched jfWeekly">
      <label data-i18n="jobs.schedule.weekdays">${t("jobs.schedule.weekdays")}</label>
      <div class="weekday-chips" id="jfWeekdays">
        ${wdNames.map((n, i) => `<label class="wd-chip"><input type="checkbox" value="${i}" ${weekdays.includes(i) ? "checked" : ""} onchange="updateNextPreview()"/><span>${esc(n)}</span></label>`).join("")}
      </div>
    </div>
    <div class="field jfSched jfMonthly"><label data-i18n="jobs.schedule.day_of_month">${t("jobs.schedule.day_of_month")}</label><select name="scheduleDayOfMonth" onchange="updateNextPreview()">${dayOpts}</select></div>

    <div class="field full divider"></div>
    <div class="field"><label data-i18n="jobs.retention_days">${t("jobs.retention_days")}</label><input name="retentionDays" type="number" min="1" value="${j.retentionDays ?? 7}" /></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="enabled" ${j.enabled !== false ? "checked" : ""}/> <span data-i18n="jobs.enabled">${t("jobs.enabled")}</span></label></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveJob(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal("jobs.add", body, foot);
  filterVmOptions();
  toggleScheduleFields();
  updateNextPreview();
}

function toggleScheduleFields() {
  const st = $("#jfScheduleType").value;
  $$(".jfSched").forEach(el => el.style.display = "none");
  if (st === "manual") return;
  $$(".jfTime, .jfTz").forEach(el => el.style.display = "");
  if (st === "weekly") $(".jfWeekly").style.display = "";
  if (st === "monthly") $(".jfMonthly").style.display = "";
}

function selectedWeekdays() { return $$("#jfWeekdays input:checked").map(c => Number(c.value)); }

// Lightweight next-run preview computed client-side (UX hint).
function computeNextRun(st, time, tzId, weekdays, dayOfMonth) {
  if (st === "manual") return null;
  const [hh, mm] = (time || "00:00").split(":").map(Number);
  const now = new Date();
  const ref = tzId ? new Date(now.toLocaleString("en-US", { timeZone: tzId })) : now;
  for (let i = 0; i < 366; i++) {
    const d = new Date(ref.getFullYear(), ref.getMonth(), ref.getDate() + i, hh || 0, mm || 0, 0, 0);
    if (d <= ref) continue;
    if (st === "daily") return d;
    if (st === "weekly" && weekdays.includes(d.getDay())) return d;
    if (st === "monthly" && d.getDate() === Number(dayOfMonth)) return d;
  }
  return null;
}

function updateNextPreview() {
  const span = $("#jfNextPreview"); if (!span) return;
  const st = $("#jfScheduleType").value;
  if (st === "manual") { span.textContent = ""; return; }
  const time = $("#jf input[name=scheduleTime]").value;
  const tzId = $("#jf select[name=timeZone]").value;
  const next = computeNextRun(st, time, tzId, selectedWeekdays(), Number($("#jf select[name=scheduleDayOfMonth]").value));
  if (!next) { span.textContent = ""; return; }
  try {
    const lbl = next.toLocaleString(currentLocale(), { weekday: "short", day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", timeZone: tzId || undefined }) + (tzId ? ` (${tzId})` : "");
    span.textContent = `${t("jobs.next_run")}: ${lbl}`;
    span.style.color = "var(--accent-2)";
  } catch { span.textContent = ""; }
}

function detectTimeZone() {
  try { const id = Intl.DateTimeFormat().resolvedOptions().timeZone; if (id) return id; } catch {}
  return "UTC";
}

function commonTimeZones(detected) {
  const list = [
    "America/Sao_Paulo", "America/New_York", "America/Chicago", "America/Los_Angeles",
    "America/Toronto", "Europe/London", "Europe/Paris", "Europe/Berlin", "Europe/Madrid",
    "Atlantic/Canary", "Asia/Tokyo", "Asia/Dubai", "Australia/Sydney", "UTC"
  ];
  if (detected && !list.includes(detected)) list.unshift(detected);
  return list.map(id => ({ id, label: id === detected ? `${id} — ${t("jobs.schedule.detected")}` : id }));
}

function weekdaysLabel(csv) {
  const names = [t("days.sun"), t("days.mon"), t("days.tue"), t("days.wed"), t("days.thu"), t("days.fri"), t("days.sat")];
  return String(csv || "").split(",").filter(Boolean).map(Number).sort().map(d => names[d] || d).join(", ");
}

function fmtInTz(iso, tzId) {
  if (!iso) return `<span class="muted">${t("common.never")}</span>`;
  const d = new Date(iso); if (isNaN(d)) return esc(iso);
  try {
    return d.toLocaleString(currentLocale(), { dateStyle: "short", timeStyle: "short", timeZone: tzId || undefined }) + (tzId ? ` <span class="muted">(${tzId})</span>` : "");
  } catch { return d.toLocaleString(currentLocale(), { dateStyle: "short", timeStyle: "short" }); }
}
function filterVmOptions() {
  const host = $("#jfHost").value;
  const opts = $$("#jfVm option");
  opts.forEach(o => {
    const match = !host || o.dataset.host == host;
    o.hidden = !match;
  });
  // Preserve the current selection when it is still valid; only fall back to
  // the first visible VM when the selected one is hidden (e.g. host changed).
  // Otherwise editing a job would always reset the VM to the first in the list.
  const sel = opts.find(o => o.selected);
  if (!sel || sel.hidden) {
    const firstVisible = opts.find(o => !o.hidden);
    if (firstVisible) firstVisible.selected = true;
  }
}
async function saveJob(id) {
  const f = $("#jf"); const fd = new FormData(f);
  const scheduleType = fd.get("scheduleType") || "manual";
  const payload = {
    name: fd.get("name"), hostId: Number(fd.get("hostId")), vmId: Number(fd.get("vmId")),
    storageId: Number(fd.get("storageId")), type: fd.get("type"),
    scheduleType,
    scheduleTime: fd.get("scheduleTime") || "00:00",
    scheduleWeekdays: scheduleType === "weekly" ? selectedWeekdays().join(",") : "",
    scheduleDayOfMonth: scheduleType === "monthly" ? Number(fd.get("scheduleDayOfMonth")) : null,
    timeZone: fd.get("timeZone") || "UTC",
    retentionDays: Number(fd.get("retentionDays")),
    enabled: f.enabled.checked
  };
  try {
    if (id) { await api.put(`/api/jobs/${id}`, payload); toast(t("toast.success"), "ok"); }
    else { await api.post("/api/jobs", payload); toast(t("toast.created"), "ok"); }
    closeModal(); router();
  } catch (e) { toast(e.message, "err"); }
}
async function runJob(id) {
  try { await api.post(`/api/jobs/${id}/run-now`); toast(t("toast.queued"), "ok"); location.hash = "#backups"; router(); }
  catch (e) { toast(e.message, "err"); }
}
async function delJob(id, name) {
  if (!await confirmModal(`${t("common.confirm_delete")}\n${name}`, { titleKey: "common.delete", confirmKey: "common.delete" })) return;
  try { await api.del(`/api/jobs/${id}`); toast(t("toast.deleted"), "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* BACKUPS (history)                                                      */
/* ===================================================================== */
async function viewBackups() {
  setTopbar("backups.title", `<button class="btn" onclick="router()">${IC("refresh-cw", 16)} <span data-i18n="common.refresh">${t("common.refresh")}</span></button>`);
  const rows = await api.get("/api/backups?limit=200");
  let html = `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="backups.vm">${t("backups.vm")}</th>
      <th data-i18n="backups.host">${t("backups.host")}</th>
      <th data-i18n="backups.type">${t("backups.type")}</th>
      <th data-i18n="backups.size">${t("backups.size")}</th>
      <th data-i18n="backups.duration">${t("backups.duration")}</th>
      <th data-i18n="backups.completed">${t("backups.completed")}</th>
      <th data-i18n="backups.error">${t("backups.error")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!rows.length) html += emptyRow(9, "archive", "backups.empty");
  else html += rows.map(r => `<tr>
    <td>${statusBadge(r.status)}</td>
    <td><strong>${esc(r.vmName)}</strong><div class="cell-mono muted">${esc(r.jobName || "")}</div></td>
    <td class="cell-dim">${esc(r.hostName)}</td>
    <td>${esc(t(`jobs.types.${r.type}`))}</td>
    <td class="cell-mono">${fmtBytes(r.sizeBytes)}</td>
    <td class="cell-mono">${fmtDuration(r.durationSeconds)}</td>
    <td class="cell-mono">${fmtRelative(r.completedAt || r.startedAt || r.queuedAt)}</td>
    <td class="cell-dim" style="max-width:240px;overflow:hidden;text-overflow:ellipsis" title="${esc(r.error || "")}">${esc(r.error || "")}</td>
    <td class="t-actions">
      <button class="btn sm icon-only" onclick="verifyBackup(${r.id})" title="${esc(t("common.verify"))}">${IC("shield-check", 14)}</button>
      <button class="btn sm icon-only" onclick="restoreFromBackup(${r.id})" title="${esc(t("common.restore"))}" ${r.status !== "succeeded" ? "disabled" : ""}>${IC("rotate-ccw", 14)}</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function verifyBackup(id) {
  try { await api.post(`/api/backups/${id}/verify`); toast(t("toast.queued"), "ok"); location.hash = "#verifications"; router(); }
  catch (e) { toast(e.message, "err"); }
}

// ---- Restore wizard state ----
let _rfVms = [];
let _rfHosts = [];
let _rfStorages = [];     // configured storage/vault targets, to derive a default destination
let _rfStorageBase = "";  // resolved storage path that owns the selected restore point
let _rpList = [];        // restore points loaded for the currently selected VM
let _rpChainPath = "";   // chain directory resolved from the selected point (sent to the agent)

// The agent serializes BackupType as an integer (0=Full,1=Incremental). Be tolerant.
function rpTypeLabel(v) {
  const s = String(v ?? "").toLowerCase();
  if (v === 0 || s === "full") return t("jobs.types.full");
  if (v === 1 || s === "incremental" || s === "inc") return t("jobs.types.incremental");
  return esc(v);
}
function rpTypeBadge(v) { return `<span class="badge unknown"><span class="dot"></span>${rpTypeLabel(v)}</span>`; }
function modeBadge(mode) {
  const m = mode || "new_vm";
  const labelKey = `restore.modes.${m}`;
  const label = t(labelKey) === labelKey ? m : t(labelKey);
  const cls = m === "disk_only" ? "unknown" : "online";
  return `<span class="badge ${cls}"><span class="dot"></span>${esc(label)}</span>`;
}

// Entry point from the Backups view: prefill the wizard with the VM that owns this backup.
async function restoreFromBackup(backupId) {
  const b = await api.get(`/api/backups/${backupId}`);
  restoreForm({
    sourceVmId: b.vmId,
    targetHostId: b.hostId,
    targetBackupId: b.backupId,
    restorePointPath: b.resultPath,
    backupRunId: b.id,
  });
}

/* ===================================================================== */
/* VERIFICATIONS                                                          */
/* ===================================================================== */
async function viewVerifications() {
  setTopbar("verifications.title", `<button class="btn primary" onclick="verifyForm()">${IC("plus", 16)} <span data-i18n="verifications.new">${t("verifications.new")}</span></button>`);
  const rows = await api.get("/api/verifications");
  let html = `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="verifications.kind">${t("verifications.kind")}</th>
      <th data-i18n="common.host">${t("common.host")}</th>
      <th data-i18n="verifications.valid">${t("verifications.valid")}</th>
      <th data-i18n="verifications.target_path">${t("verifications.target_path")}</th>
      <th data-i18n="backups.completed">${t("backups.completed")}</th>
    </tr></thead><tbody>`;
  if (!rows.length) html += emptyRow(6, "shield-check", "verifications.empty");
  else html += rows.map(v => `<tr>
    <td>${statusBadge(v.status)}</td>
    <td>${esc(t(`verifications.kinds.${v.kind}`))}</td>
    <td class="cell-dim">${esc(v.hostName)}</td>
    <td>${v.isValid === null ? `<span class="muted">—</span>` : (v.isValid ? `<span class="badge online"><span class="dot"></span>${t("status.succeeded")}</span>` : `<span class="badge failed"><span class="dot"></span>${t("status.failed")}</span>`)}</td>
    <td class="cell-mono" title="${esc(v.targetPath)}">${esc(v.targetPath)}</td>
    <td class="cell-mono">${fmtRelative(v.completedAt || v.startedAt || v.queuedAt)}</td>
  </tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function verifyForm() {
  const jobs = await api.get("/api/jobs");
  if (!jobs.length) { toast(t("verifications.no_jobs"), "err"); location.hash = "#jobs"; return; }
  const opts = jobs.map(j => `<option value="${j.id}">${esc(j.name)} — ${esc(j.vmName)}</option>`).join("");
  const body = `<form id="vf" class="form-grid">
    <div class="field full"><label data-i18n="verifications.select_job">${t("verifications.select_job")}</label>
      <select name="jobId" required>${opts}</select></div>
    <p class="muted form-hint" data-i18n="verifications.job_hint">${t("verifications.job_hint")}</p>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitVerify()"><span data-i18n="common.verify">${t("common.verify")}</span></button>`;
  openModal("verifications.new", body, foot);
}
async function submitVerify() {
  const f = $("#vf"); const fd = new FormData(f);
  try { await api.post(`/api/jobs/${Number(fd.get("jobId"))}/verify`); toast(t("toast.queued"), "ok"); closeModal(); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* RESTORES                                                               */
/* ===================================================================== */
async function viewRestores() {
  setTopbar("restore.title", `<button class="btn primary" onclick="restoreForm()">${IC("plus", 16)} <span data-i18n="restore.new">${t("restore.new")}</span></button>`);
  const rows = await api.get("/api/restores");
  let html = `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="restore.mode">${t("restore.mode")}</th>
      <th data-i18n="restore.source_vm">${t("restore.source_vm")}</th>
      <th data-i18n="restore.target_host">${t("restore.target_host")}</th>
      <th data-i18n="restore.new_name">${t("restore.new_name")}</th>
      <th data-i18n="restore.destination">${t("restore.destination")}</th>
      <th data-i18n="backups.completed">${t("backups.completed")}</th>
      <th data-i18n="backups.error">${t("backups.error")}</th>
    </tr></thead><tbody>`;
  if (!rows.length) html += emptyRow(8, "rotate-ccw", "restore.empty");
  else html += rows.map(r => `<tr>
    <td>${statusBadge(r.status)}</td>
    <td>${modeBadge(r.mode)}</td>
    <td><strong>${esc(r.sourceVmName || "—")}</strong></td>
    <td class="cell-dim">${esc(r.targetHostName)}</td>
    <td>${esc(r.newName)}</td>
    <td class="cell-mono">${esc(r.destination)}</td>
    <td class="cell-mono">${fmtRelative(r.completedAt || r.startedAt || r.queuedAt)}</td>
    <td class="cell-dim" style="max-width:240px;overflow:hidden;text-overflow:ellipsis" title="${esc(r.error || "")}">${esc(r.error || "")}</td>
  </tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function restoreForm(prefill = {}) {
  const [hosts, vms, storages] = await Promise.all([api.get("/api/hosts"), api.get("/api/vms"), api.get("/api/storages")]);
  if (!hosts.length) { toast(t("restore.no_vms"), "err"); location.hash = "#hosts"; return; }
  if (!vms.length) { toast(t("restore.no_vms"), "err"); location.hash = "#vms"; return; }
  _rfHosts = hosts; _rfVms = vms; _rfStorages = Array.isArray(storages) ? storages : [];
  _rfStorageBase = "";

  const vmOpts = vms.map(v => `<option value="${v.id}" data-host="${v.hostId}" ${prefill.sourceVmId === v.id ? "selected" : ""}>${esc(v.name)} — ${esc(v.hostName)}</option>`).join("");
  const hostOpts = hosts.map(h => `<option value="${h.id}" ${prefill.targetHostId === h.id ? "selected" : ""}>${esc(h.name)}</option>`).join("");
  const newVmChecked = (prefill.mode || "new_vm") === "new_vm" ? "checked" : "";
  const diskChecked = prefill.mode === "disk_only" ? "checked" : "";
  const fileChecked = prefill.mode === "file_level" ? "checked" : "";

  const body = `<form id="rf" class="form-grid">
    <div class="field full"><div class="warn-box" data-i18n="restore.safety_warning">${t("restore.safety_warning")}</div></div>

    <div class="field full">
      <label data-i18n="restore.source_vm">${t("restore.source_vm")}</label>
      <select name="sourceVmId" id="rfVm" onchange="onRestoreVmChanged()" required>${vmOpts}</select>
      <span class="hint" data-i18n="restore.select_vm">${t("restore.select_vm")}</span>
    </div>

    <div class="field full">
      <label data-i18n="restore.restore_points">${t("restore.restore_points")}</label>
      <div id="rfPoints" class="rp-list"><span class="hint" data-i18n="restore.loading_restore_points">${t("restore.loading_restore_points")}</span></div>
    </div>

    <div class="field full">
      <label data-i18n="restore.mode">${t("restore.mode")}</label>
      <div class="radio-cards">
        <label class="radio-card">
          <input type="radio" name="mode" value="new_vm" ${newVmChecked} onchange="toggleRestoreMode()" />
          <span class="rc-title">${IC("monitor", 16)} <span data-i18n="restore.mode.new_vm">${t("restore.mode.new_vm")}</span></span>
          <span class="rc-desc" data-i18n="restore.mode.new_vm_desc">${t("restore.mode.new_vm_desc")}</span>
        </label>
        <label class="radio-card">
          <input type="radio" name="mode" value="disk_only" ${diskChecked} onchange="toggleRestoreMode()" />
          <span class="rc-title">${IC("hard-drive", 16)} <span data-i18n="restore.mode.disk_only">${t("restore.mode.disk_only")}</span></span>
          <span class="rc-desc" data-i18n="restore.mode.disk_only_desc">${t("restore.mode.disk_only_desc")}</span>
        </label>
        <label class="radio-card">
          <input type="radio" name="mode" value="file_level" ${fileChecked} onchange="toggleRestoreMode()" />
          <span class="rc-title">${IC("folder", 16)} <span data-i18n="restore.mode.file_level">${t("restore.mode.file_level")}</span></span>
          <span class="rc-desc" data-i18n="restore.mode.file_level_desc">${t("restore.mode.file_level_desc")}</span>
        </label>
      </div>
    </div>

    <div class="field rf-vmops">
      <label data-i18n="restore.target_host">${t("restore.target_host")}</label>
      <select name="targetHostId" id="rfTargetHost" required>${hostOpts}</select>
      <span class="hint" data-i18n="restore.target_host_hint">${t("restore.target_host_hint")}</span>
    </div>

    <div class="field rf-vmops">
      <label id="rfNewNameLabel" data-i18n="restore.new_name">${t("restore.new_name")}</label>
      <input name="newName" id="rfNewName" required oninput="this.dataset.touched='1'" />
    </div>

    <div class="field full rf-vmops">
      <label data-i18n="restore.destination">${t("restore.destination")}</label>
      <input name="destination" id="rfDestination" required oninput="this.dataset.touched='1'" />
      <span class="hint" id="rfDestHint"></span>
    </div>

    <div class="field full rf-newvm-only">
      <label class="check"><input type="checkbox" name="overwriteExisting" id="rfOverwrite" /> <span data-i18n="restore.overwrite">${t("restore.overwrite")}</span></label>
    </div>

    <input type="hidden" name="sourceHostId" id="rfSourceHost" />
    <input type="hidden" name="backupRunId" value="${prefill.backupRunId || ""}" />
  </form>`;

  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitRestore()"><span id="rfSubmitLabel" data-i18n="common.restore">${t("common.restore")}</span></button>`;
  openModal("restore.new", body, foot);

  _rpList = [];
  _rpChainPath = prefill.restorePointPath || "";
  await onRestoreVmChanged(prefill.targetBackupId);
  toggleRestoreMode();
}

async function onRestoreVmChanged(selectBackupId = null) {
  const f = $("#rf"); if (!f) return;
  const vmId = Number(f.sourceVmId.value);
  const vm = _rfVms.find(v => v.id === vmId);
  $("#rfSourceHost").value = vm ? String(vm.hostId) : "";
  const th = $("#rfTargetHost");
  if (th && vm) th.value = String(vm.hostId);
  const nameInput = $("#rfNewName");
  if (nameInput) nameInput.dataset.touched = ""; // re-apply defaults on VM change
  const destInput = $("#rfDestination");
  if (destInput) destInput.dataset.touched = ""; // re-apply suggested path on VM change
  await loadRestorePoints(vm, selectBackupId);
  toggleRestoreMode();
}

async function loadRestorePoints(vm, selectBackupId = null) {
  const box = $("#rfPoints");
  if (!box) return;
  if (!vm) { _rpList = []; box.innerHTML = `<span class="hint">${t("restore.no_restore_points")}</span>`; return; }
  box.innerHTML = `<span class="hint" data-i18n="restore.loading_restore_points">${t("restore.loading_restore_points")}</span>`;
  try {
    const points = await api.get(`/api/hosts/${vm.hostId}/vms/${vm.id}/restore-points`);
    _rpList = Array.isArray(points) ? points : [];
    renderRestorePoints(_rpList, selectBackupId);
  } catch (e) {
    _rpList = [];
    box.innerHTML = `<span class="hint" style="color:var(--err)">${esc(e.message || t("restore.load_failed"))}</span>`;
  }
}

function renderRestorePoints(points, selectBackupId = null) {
  const box = $("#rfPoints");
  if (!points.length) { box.innerHTML = `<span class="hint">${t("restore.no_restore_points")}</span>`; _rpChainPath = ""; return; }
  const sorted = [...points].sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
  const preselect = selectBackupId && points.some(p => p.backupId === selectBackupId)
    ? selectBackupId
    : sorted[sorted.length - 1].backupId;
  box.innerHTML = `<div class="hint" style="margin-bottom:6px" data-i18n="restore.pick_point">${t("restore.pick_point")}</div>` +
    sorted.map((p, i) => {
      const isLatest = i === sorted.length - 1;
      const checked = p.backupId === preselect ? "checked" : "";
      const size = p.sizeBytes ? fmtBytes(p.sizeBytes) : "—";
      return `<label class="rp-card ${checked ? "selected" : ""}">
        <input type="radio" name="rpChoice" value="${esc(p.backupId)}" ${checked} onchange="onRestorePointChanged()" />
        <span class="rp-type">${rpTypeBadge(p.type)}</span>
        <span class="rp-main"><strong>${esc(p.backupId)}</strong>${isLatest ? ` <span class="badge unknown"><span class="dot"></span>${t("restore.latest")}</span>` : ""}<br><span class="muted cell-mono">${fmtDate(p.createdAt)}</span></span>
        <span class="rp-size">${size}</span>
      </label>`;
    }).join("");
  onRestorePointChanged();
}

function onRestorePointChanged() {
  const f = $("#rf");
  const chosen = f && f.querySelector('input[name="rpChoice"]:checked');
  if (!chosen) { _rpChainPath = ""; return; }
  const p = _rpList.find(x => x.backupId === chosen.value);
  _rpChainPath = p ? (p.chainPath || p.restorePointPath || "") : "";
  // Re-derive the destination base from the newly selected point and re-apply
  // the suggestion if the operator hasn't typed a custom path yet.
  _rfStorageBase = resolveStorageBase(_rpChainPath);
  const destInput = $("#rfDestination");
  if (destInput && !destInput.dataset.touched) {
    const mode = ((f && (f.querySelector('input[name="mode"]:checked') || {}).value)) || "new_vm";
    const vm = _rfVms.find(v => v.id === Number(f.sourceVmId.value));
    destInput.value = defaultRestoreDestination(vm ? vm.name : "restored", mode);
  }
  $$('#rf .rp-card').forEach(el => el.classList.remove('selected'));
  const card = chosen.closest('.rp-card');
  if (card) card.classList.add('selected');
}

// Resolve which configured storage (vault) owns the selected restore point, so the
// suggested destination derives from the backup's own storage path rather than a
// hardcoded guess. Falls back to "" when no storage path is a prefix of the chain.
function resolveStorageBase(chainPath) {
  if (!_rfStorages || !chainPath) return "";
  const norm = s => String(s || "").replace(/\\/g, "/").replace(/\/+$/,
"").toLowerCase();
  const target = norm(chainPath);
  if (!target) return "";
  let best = null, bestLen = -1;
  for (const s of _rfStorages) {
    const sp = norm(s.path);
    if (!sp) continue;
    if (target === sp || target.startsWith(sp + "/")) {
      if (sp.length > bestLen) { best = s; bestLen = sp.length; }
    }
  }
  return best ? best.path.replace(/[\\/]+$/, "") : "";
}

// Default restore destination on the target host. Prefers the owning storage's
// path (+ a "Restores" subfolder); falls back to a conventional local path when
// the chain can't be matched to a configured storage.
function defaultRestoreDestination(baseName, mode) {
  const sub = mode === "disk_only" ? `${baseName}-disk` : `${baseName}-restored`;
  const root = _rfStorageBase || "C:\\ProgramData\\HyperVault";
  return `${root}\\Restores\\${sub}`;
}

function toggleRestoreMode() {
  const f = $("#rf"); if (!f) return;
  const mode = ((f.querySelector('input[name="mode"]:checked') || {}).value) || "new_vm";
  const diskOnly = mode === "disk_only";
  const fileLevel = mode === "file_level";
  $$('#rf .rf-newvm-only').forEach(el => el.style.display = (diskOnly || fileLevel) ? 'none' : '');
  $$('#rf .rf-vmops').forEach(el => el.style.display = fileLevel ? 'none' : '');
  const submitLabel = $("#rfSubmitLabel");
  if (submitLabel) submitLabel.textContent = fileLevel ? t("restore.flr.open") : t("common.restore");
  if (fileLevel) return; // no destination / disk name needed for file-level restore

  const vm = _rfVms.find(v => v.id === Number(f.sourceVmId.value));
  const baseName = vm ? vm.name : "restored";
  _rfStorageBase = resolveStorageBase(_rpChainPath); // re-resolve in case the point changed
  const nameInput = $("#rfNewName");
  const nameLabel = $("#rfNewNameLabel");
  const destInput = $("#rfDestination");
  const destHint = $("#rfDestHint");
  const autoNote = t("restore.dest_auto_note");
  if (diskOnly) {
    if (nameLabel) nameLabel.textContent = t("restore.disk_prefix");
    if (destHint) destHint.textContent = `${t("restore.dest_hint_disk_only")} ${autoNote}`;
    if (nameInput && !nameInput.dataset.touched) nameInput.value = `${baseName}-disk`;
  } else {
    if (nameLabel) nameLabel.textContent = t("restore.new_name");
    if (destHint) destHint.textContent = `${t("restore.dest_hint_new_vm")} ${autoNote}`;
    if (nameInput && !nameInput.dataset.touched) nameInput.value = `${baseName}-restored`;
  }
  // Pre-fill a suggested path until the operator edits it (dataset.touched gate).
  if (destInput && !destInput.dataset.touched) destInput.value = defaultRestoreDestination(baseName, mode);
}

async function submitRestore() {
  const f = $("#rf");
  const mode = ((f.querySelector('input[name="mode"]:checked') || {}).value) || "new_vm";

  // File-level restore is an interactive session (mount read-only + browse),
  // not a queued job. Validate only the restore point, then open the explorer.
  if (mode === "file_level") {
    const chosen = f.querySelector('input[name="rpChoice"]:checked');
    if (!chosen || !_rpChainPath) { toast(t("restore.no_restore_points"), "err"); return; }
    const fd = new FormData(f);
    const vmId = Number(fd.get("sourceVmId"));
    const vm = _rfVms.find(v => v.id === vmId);
    const hostId = vm ? vm.hostId : Number(fd.get("sourceHostId"));
    if (!hostId) { toast(t("restore.no_vms"), "err"); return; }
    await openFlrExplorer(hostId, _rpChainPath, chosen.value);
    return;
  }

  if (!f.reportValidity()) return;
  const fd = new FormData(f);
  const overwrite = f.overwriteExisting.checked;
  if (mode === "new_vm" && overwrite && !await confirmModal(t("restore.confirm"), { titleKey: "restore.title", confirmKey: "common.restore", danger: true })) { return; }

  const chosen = f.querySelector('input[name="rpChoice"]:checked');
  const chosenBackupId = chosen ? chosen.value : null;
  const vmId = Number(fd.get("sourceVmId"));
  const vm = _rfVms.find(v => v.id === vmId);

  const payload = {
    sourceHostId: Number(fd.get("sourceHostId")) || (vm ? vm.hostId : 0),
    targetHostId: Number(fd.get("targetHostId")),
    restorePointPath: _rpChainPath || "",
    destination: fd.get("destination"),
    newName: fd.get("newName") || "",
    targetBackupId: chosenBackupId || null,
    overwriteExisting: overwrite,
    backupRunId: fd.get("backupRunId") ? Number(fd.get("backupRunId")) : null,
    mode,
    sourceVmId: vmId || null,
    sourceVmName: vm ? vm.name : null,
  };
  if (!payload.restorePointPath) { toast(t("restore.no_restore_points"), "err"); return; }
  try { await api.post("/api/restore", payload); toast(t("toast.queued"), "ok"); closeModal(); location.hash = "#restores"; router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* FILE-LEVEL RESTORE (FLR) explorer                                       */
/* Mounts the selected chain read-only on the owning agent and lets the    */
/* operator browse volumes/folders and download individual files. The      */
/* browser only talks to the manager, which proxies to the agent.          */
/* ===================================================================== */
let _flr = null;      // { hostId, sessionId, expiresAt, volumes, volumeId, path:[], entries:[], restorePointPath, targetBackupId }
let _flrTimer = null; // expiry countdown interval

async function openFlrExplorer(hostId, restorePointPath, targetBackupId) {
  let cancelled = false;
  openModal("restore.flr.title", `<div class="loader" style=\"padding:30px\"><span class="spinner"></span><span data-i18n="restore.flr.mounting">${t("restore.flr.mounting")}</span></div>`, "");
  _modalCloseHook = () => { cancelled = true; };
  try {
    const session = await api.post(`/api/hosts/${hostId}/flr/sessions`, { restorePointPath, targetBackupId: targetBackupId || null });
    if (cancelled) {
      // Operator closed the modal while the disk was being mounted; tear it down.
      if (session && session.sessionId) api.del(`/api/hosts/${hostId}/flr/sessions/${encodeURIComponent(session.sessionId)}`).catch(() => {});
      return;
    }
    if (!session || !session.sessionId || !Array.isArray(session.volumes) || !session.volumes.length) {
      throw new Error(t("restore.flr.mount_error"));
    }
    _flr = {
      hostId, sessionId: session.sessionId, expiresAt: session.expiresAt,
      volumes: session.volumes, volumeId: session.volumes[0].volumeId, path: [], entries: [],
      restorePointPath, targetBackupId: targetBackupId || null
    };
    renderFlrExplorer();
  } catch (e) {
    if (cancelled) return;
    closeModal();
    toast(e.message || t("restore.flr.mount_error"), "err");
  }
}

function renderFlrExplorer() {
  const s = _flr; if (!s) return;
  const volOpts = s.volumes.map(v => `<option value="${esc(v.volumeId)}" ${v.volumeId === s.volumeId ? "selected" : ""}>${esc(volLabel(v))}</option>`).join("");
  const body = `
    <div class="flr-bar">
      <div class="flr-meta">
        <select id="flrVolume" onchange="flrVolumeChanged()">${volOpts}</select>
        <span class="flr-expires" id="flrExpires"></span>
      </div>
      <button class="btn ghost sm" onclick="closeFlrExplorerAndModal()">${IC("x", 14)} <span data-i18n="restore.flr.close">${t("restore.flr.close")}</span></button>
    </div>
    <div class="flr-crumbs" id="flrCrumbs">${flrBreadcrumb()}</div>
    <div class="flr-list" id="flrList">${pageLoader()}</div>`;
  const foot = `<button class="btn ghost" data-close data-i18n="restore.flr.close">${t("restore.flr.close")}</button>`;
  openModal("restore.flr.title", body, foot);
  _modalCloseHook = closeFlrExplorer; // tear the session down on Esc/backdrop/close
  flrStartTimer();
  flrLoadDir();
}

function volLabel(v) {
  const parts = [];
  if (v && v.label) parts.push(v.label);
  if (v && v.fileSystem) parts.push(`(${v.fileSystem})`);
  if (!parts.length) parts.push((v && v.volumeId) || "Volume");
  return `${parts.join(" ")} · ${fmtBytes(v && v.sizeBytes)}`;
}

function flrBreadcrumb() {
  const s = _flr; if (!s) return "";
  let html = `<span class="crumb" onclick="flrGoto(-1)">${IC("hard-drive", 13)} <span data-i18n="restore.flr.root">${t("restore.flr.root")}</span></span>`;
  s.path.forEach((seg, i) => {
    html += `<span class="crumb-sep">›</span><span class="crumb" onclick="flrGoto(${i})">${esc(seg)}</span>`;
  });
  return html;
}

async function flrLoadDir() {
  const box = $("#flrList"); if (!box || !_flr) return;
  box.innerHTML = pageLoader();
  const pathStr = _flr.path.join("\\");
  const url = `/api/hosts/${_flr.hostId}/flr/sessions/${encodeURIComponent(_flr.sessionId)}/ls?volumeId=${encodeURIComponent(_flr.volumeId)}${pathStr ? `&path=${encodeURIComponent(pathStr)}` : ""}`;
  try {
    const entries = await api.get(url);
    _flr.entries = Array.isArray(entries) ? entries : [];
    flrRenderList();
  } catch (e) {
    if (e && e.status === 404) { flrShowExpired(); return; }
    box.innerHTML = `<div class="empty">${IC("alert-triangle", 26)}<div>${esc((e && e.message) || t("toast.network_error"))}</div></div>`;
    paint(box);
  }
}

function flrRenderList() {
  const box = $("#flrList"); if (!box || !_flr) return;
  const s = _flr;
  const entries = s.entries || [];
  let html = "";
  if (s.path.length) html += `<div class="flr-row flr-up" onclick="flrUp()">${IC("arrow-up", 15)} <span class="flr-name muted">..</span></div>`;
  if (!entries.length && !s.path.length) {
    box.innerHTML = `<div class="empty">${IC("folder", 28)}<div data-i18n="restore.flr.empty">${t("restore.flr.empty")}</div></div>`;
    paint(box);
    return;
  }
  for (const e of entries) {
    if (e.isDirectory) {
      html += `<div class="flr-row" onclick="flrOpenFolder(this.dataset.name)" data-name="${esc(e.name)}">${IC("folder", 16)} <span class="flr-name">${esc(e.name)}</span></div>`;
    } else {
      const url = flrFileUrl(e.relativePath);
      html += `<div class="flr-row file">
        ${IC("file", 16)}
        <span class="flr-name" title="${esc(e.name)}">${esc(e.name)}</span>
        <span class="flr-size">${fmtBytes(e.sizeBytes)}</span>
        <span class="flr-date">${fmtDate(e.lastWriteTimeUtc)}</span>
        <a class="btn sm icon-only" href="${url}" download="${esc(e.name)}" title="${esc(t("restore.flr.download"))}" onclick="event.stopPropagation()">${IC("download", 14)}</a>
      </div>`;
    }
  }
  box.innerHTML = html;
  paint(box);
}

function flrFileUrl(relPath) {
  const s = _flr; if (!s || !relPath) return "#";
  return `/api/hosts/${s.hostId}/flr/sessions/${encodeURIComponent(s.sessionId)}/get?volumeId=${encodeURIComponent(s.volumeId)}&path=${encodeURIComponent(relPath)}`;
}

function flrGoto(i) {
  if (!_flr) return;
  _flr.path = i < 0 ? [] : _flr.path.slice(0, i + 1);
  const c = $("#flrCrumbs"); if (c) c.innerHTML = flrBreadcrumb();
  flrLoadDir();
}
function flrUp() {
  if (!_flr || !_flr.path.length) return;
  _flr.path.pop();
  const c = $("#flrCrumbs"); if (c) c.innerHTML = flrBreadcrumb();
  flrLoadDir();
}
function flrOpenFolder(name) {
  if (!_flr || !name) return;
  _flr.path.push(name);
  const c = $("#flrCrumbs"); if (c) c.innerHTML = flrBreadcrumb();
  flrLoadDir();
}
function flrVolumeChanged() {
  if (!_flr) return;
  const sel = $("#flrVolume");
  _flr.volumeId = sel ? sel.value : _flr.volumeId;
  _flr.path = [];
  const c = $("#flrCrumbs"); if (c) c.innerHTML = flrBreadcrumb();
  flrLoadDir();
}

function flrStartTimer() {
  if (_flrTimer) clearInterval(_flrTimer);
  flrTick();
  _flrTimer = setInterval(flrTick, 15000);
}
function flrTick() {
  const el = $("#flrExpires"); if (!el || !_flr) return;
  const ms = new Date(_flr.expiresAt).getTime() - Date.now();
  if (isNaN(ms) || ms <= 0) { el.textContent = t("restore.flr.expired"); el.classList.add("danger"); return; }
  const mins = Math.max(0, Math.floor(ms / 60000));
  el.textContent = `${t("restore.flr.expires_in")} ${mins}m`;
  el.classList.remove("danger");
}

function flrShowExpired() {
  const box = $("#flrList"); if (!box || !_flr) return;
  box.innerHTML = `<div class="empty">${IC("alert-triangle", 28)}<div data-i18n="restore.flr.session_expired_msg">${t("restore.flr.session_expired_msg")}</div>
    <button class="btn primary sm" style="margin-top:12px" onclick="flrRecreate()"><span data-i18n="restore.flr.new_session">${t("restore.flr.new_session")}</span></button></div>`;
  paint(box);
}
function flrRecreate() {
  if (!_flr) return;
  const args = { hostId: _flr.hostId, restorePointPath: _flr.restorePointPath, targetBackupId: _flr.targetBackupId };
  closeFlrExplorer();
  openFlrExplorer(args.hostId, args.restorePointPath, args.targetBackupId);
}

function closeFlrExplorer() {
  if (_flrTimer) { clearInterval(_flrTimer); _flrTimer = null; }
  const s = _flr; _flr = null;
  if (s && s.sessionId) api.del(`/api/hosts/${s.hostId}/flr/sessions/${encodeURIComponent(s.sessionId)}`).catch(() => {});
}
function closeFlrExplorerAndModal() { closeFlrExplorer(); closeModal(); }

/* ===================================================================== */
/* SETTINGS (account + user management)                                   */
/* ===================================================================== */
async function viewSettings() {
  setTopbar("settings.title", "");
  const isAdmin = currentUser && currentUser.role === "admin";
  let html = `<div class="grid" style="grid-template-columns: 1fr; gap:24px">`;

  // ---- My account / change password ----
  html += `<div class="card">
    <div class="section-head" style="margin-top:0"><h2 data-i18n="settings.my_account">${t("settings.my_account")}</h2></div>
    <div class="kv" style="margin-bottom:18px">
      <dt data-i18n="auth.username">${t("auth.username")}</dt><dd><strong>${esc(currentUser.username)}</strong></dd>
      <dt data-i18n="common.role">${t("common.role")}</dt><dd>${statusBadge(currentUser.role === "admin" ? "succeeded" : "unknown")} ${esc(t("roles." + currentUser.role))}</dd>
    </div>
    <form id="pwf" class="form-grid" style="max-width:560px">
      <div class="field full"><label data-i18n="auth.current_password">${t("auth.current_password")}</label><input name="currentPassword" type="password" autocomplete="current-password" required /></div>
      <div class="field"><label data-i18n="auth.new_password">${t("auth.new_password")}</label><input name="newPassword" type="password" autocomplete="new-password" minlength="4" required /></div>
      <div class="field"><label data-i18n="auth.confirm_password">${t("auth.confirm_password")}</label><input name="confirmPassword" type="password" autocomplete="new-password" minlength="4" required /></div>
      <div class="field full" style="flex-direction:row;justify-content:flex-end;gap:10px">
        <button type="button" class="btn primary" onclick="changePassword()"><span data-i18n="auth.change_password">${t("auth.change_password")}</span></button>
      </div>
    </form>
  </div>`;

  if (isAdmin) {
    html += `<div class="card">
      <div class="section-head" style="margin-top:0"><h2 data-i18n="settings.users">${t("settings.users")}</h2>
        <button class="btn primary" onclick="userForm()">${IC("plus", 16)} <span data-i18n="settings.add_user">${t("settings.add_user")}</span></button></div>
      <div id="usersTableWrap">${pageLoader()}</div>
    </div>`;
  } else {
    html += `<div class="card"><div class="empty">${IC("lock", 30)}<div data-i18n="settings.admin_only">${t("settings.admin_only")}</div></div></div>`;
  }
  html += `</div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
  if (isAdmin) await renderUsersTable();
}

async function renderUsersTable() {
  const wrap = $("#usersTableWrap"); if (!wrap) return;
  try {
    const users = await api.get("/api/users");
    let html = `<div class="table-wrap"><table>
      <thead><tr>
        <th data-i18n="auth.username">${t("auth.username")}</th>
        <th data-i18n="common.role">${t("common.role")}</th>
        <th data-i18n="common.status">${t("common.status")}</th>
        <th data-i18n="common.last_login">${t("common.last_login")}</th>
        <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
      </tr></thead><tbody>`;
    if (!users.length) html += emptyRow(5, "user", "common.empty");
    else html += users.map(u => `<tr>
      <td><strong>${esc(u.username)}</strong>${u.id === currentUser.id ? ` <span class="muted">(${t("settings.you")})</span>` : ""}</td>
      <td>${esc(t("roles." + u.role))}</td>
      <td>${u.enabled ? `<span class="badge online"><span class="dot"></span>${t("status.online")}</span>` : `<span class="badge offline"><span class="dot"></span>${t("status.offline")}</span>`}</td>
      <td class="cell-mono">${fmtRelative(u.lastLoginAt)}</td>
      <td class="t-actions">
        <button class="btn sm icon-only" onclick="userForm(${u.id})" title="${esc(t("common.edit"))}">${IC("pencil", 14)}</button>
        <button class="btn sm icon-only" onclick="resetPasswordForm(${u.id},'${esc(u.username)}')" title="${esc(t("common.reset_password"))}">${IC("key-round", 14)}</button>
        <button class="btn sm icon-only danger" onclick="delUser(${u.id},'${esc(u.username)}')" title="${esc(t("common.delete"))}" ${u.id === currentUser.id ? "disabled" : ""}>${IC("trash-2", 14)}</button>
      </td></tr>`).join("");
    html += `</tbody></table></div>`;
    wrap.innerHTML = html; i18n.apply(wrap);
  } catch (e) { wrap.innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
}

async function userForm(id = null) {
  let u = { username: "", role: "user", enabled: true };
  if (id) u = await api.get("/api/users").then(l => l.find(x => x.id === id) || u);
  const body = `<form id="uf" class="form-grid">
    <div class="field full"><label data-i18n="auth.username">${t("auth.username")}</label><input name="username" value="${esc(u.username)}" required ${id ? "disabled" : ""} /></div>
    ${id ? "" : `<div class="field full"><label data-i18n="auth.password">${t("auth.password")}</label><input name="password" type="password" minlength="4" required autocomplete="new-password" /></div>`}
    <div class="field"><label data-i18n="common.role">${t("common.role")}</label><select name="role">
      <option value="user" ${u.role === "user" ? "selected" : ""}>${t("roles.user")}</option>
      <option value="admin" ${u.role === "admin" ? "selected" : ""}>${t("roles.admin")}</option></select></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="enabled" ${u.enabled ? "checked" : ""}/> <span data-i18n="common.enabled">${t("common.enabled")}</span></label></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveUser(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal(id ? "settings.edit_user" : "settings.add_user", body, foot);
}

async function saveUser(id) {
  const f = $("#uf"); const fd = new FormData(f);
  const payload = { username: fd.get("username"), role: fd.get("role"), enabled: f.enabled.checked };
  try {
    if (id) { await api.put(`/api/users/${id}`, payload); toast(t("toast.success"), "ok"); }
    else {
      const create = { username: fd.get("username"), password: fd.get("password"), role: fd.get("role"), enabled: f.enabled.checked };
      await api.post("/api/users", create); toast(t("toast.user_created"), "ok");
    }
    closeModal(); renderUsersTable();
  } catch (e) { toast(e.message, "err"); }
}

function resetPasswordForm(id, username) {
  const body = `<form id="rpf" class="form-grid">
    <div class="field full"><label data-i18n="auth.username">${t("auth.username")}</label><input value="${esc(username)}" disabled /></div>
    <div class="field full"><label data-i18n="auth.new_password">${t("auth.new_password")}</label><input name="password" type="password" minlength="4" required autocomplete="new-password" /></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitResetPassword(${id})"><span data-i18n="common.reset_password">${t("common.reset_password")}</span></button>`;
  openModal("common.reset_password", body, foot);
}

async function submitResetPassword(id) {
  const f = $("#rpf"); const fd = new FormData(f);
  try { await api.post(`/api/users/${id}/reset-password`, { password: fd.get("password") }); toast(t("toast.password_changed"), "ok"); closeModal(); }
  catch (e) { toast(e.message, "err"); }
}

async function delUser(id, username) {
  if (!await confirmModal(`${t("common.confirm_delete")}\n${username}`, { titleKey: "common.delete", confirmKey: "common.delete" })) return;
  try { await api.del(`/api/users/${id}`); toast(t("toast.user_deleted"), "ok"); renderUsersTable(); }
  catch (e) { toast(e.message, "err"); }
}

async function changePassword() {
  const f = $("#pwf"); const fd = new FormData(f);
  if (fd.get("newPassword") !== fd.get("confirmPassword")) { toast(t("auth.password_mismatch"), "err"); return; }
  try {
    await api.post("/api/auth/change-password", { currentPassword: fd.get("currentPassword"), newPassword: fd.get("newPassword") });
    toast(t("toast.password_changed"), "ok"); f.reset();
  } catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* AUTH (login / logout / session)                                        */
/* ===================================================================== */
function showLogin() {
  currentUser = null;
  $("#appShell").classList.add("locked");
  $("#loginScreen").classList.add("show");
  $("#loginError").classList.add("hidden");
  const f = $("#loginForm"); if (f) f.reset();
  const u = $("#loginForm input[name=username]"); if (u) setTimeout(() => u.focus(), 30);
  stopRefresh();
}

function showApp() {
  $("#loginScreen").classList.remove("show");
  $("#appShell").classList.remove("locked");
  updateUserChip();
}

function updateUserChip() {
  if (!currentUser) return;
  $("#userName").textContent = currentUser.username;
  $("#userRole").textContent = t("roles." + currentUser.role);
  $("#userAvatar").textContent = (currentUser.username || "?").charAt(0).toUpperCase();
}

function onUnauthorized() { showLogin(); }

async function doLogin() {
  const f = $("#loginForm"); const fd = new FormData(f);
  const err = $("#loginError");
  const btn = $("#loginForm button[type=submit]");
  err.classList.add("hidden");
  btn.disabled = true; btn.textContent = t("common.loading");
  try {
    const me = await api.post("/api/auth/login", { username: fd.get("username"), password: fd.get("password") });
    currentUser = me;
    showApp();
    toast(t("toast.logged_in"), "ok");
    if (!location.hash || location.hash === "#") location.hash = "#dashboard";
    router();
  } catch (e) {
    err.textContent = t("auth.invalid");
    err.classList.remove("hidden");
  } finally {
    btn.disabled = false; btn.textContent = t("auth.sign_in");
  }
}

async function doLogout() {
  try { await api.post("/api/auth/logout"); } catch {}
  toast(t("toast.logged_out"), "info");
  showLogin();
}

/* ===================================================================== */
/* BOOTSTRAP                                                              */
/* ===================================================================== */
function wireLangSwitch() {
  const seg = $("#langSeg");
  const sync = () => $$("#langSeg button").forEach(b => b.classList.toggle("active", b.dataset.lang === i18n.current));
  seg.addEventListener("click", (e) => {
    const btn = e.target.closest("button[data-lang]");
    if (btn) i18n.setLang(btn.dataset.lang).then(sync);
  });
  sync();
}

window.addEventListener("hashchange", () => router());
document.addEventListener("langchange", () => { wireLangSwitch(); router(true); });

window.app = { onUnauthorized };

(async () => {
  await i18n.init();
  wireLangSwitch();
  document.documentElement.setAttribute("lang", i18n.current);
  // wire login + logout
  $("#loginForm").addEventListener("submit", (e) => { e.preventDefault(); doLogin(); });
  $("#logoutBtn").addEventListener("click", () => doLogout());
  // check existing session
  try {
    currentUser = await api.get("/api/auth/me");
    showApp();
    if (!location.hash) location.hash = "#dashboard";
    router();
  } catch {
    showLogin();
  }
})();
