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
function openModal(titleKey, bodyHtml, footHtml = "") {
  $("#modalTitle").textContent = t(titleKey);
  const body = $("#modalBody"); body.innerHTML = bodyHtml;
  i18n.apply(body);
  const foot = $("#modalFoot"); foot.innerHTML = footHtml; i18n.apply(foot);
  $("#modalRoot").classList.remove("hidden");
  $("#modalRoot").setAttribute("aria-hidden", "false");
  const f = body.querySelector("input,select,textarea"); if (f) setTimeout(() => f.focus(), 30);
}
function closeModal() {
  $("#modalRoot").classList.add("hidden");
  $("#modalRoot").setAttribute("aria-hidden", "true");
}
document.addEventListener("click", (e) => { if (e.target.matches("[data-close]")) closeModal(); });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeModal(); });

/* ---------- topbar ---------- */
function setTopbar(titleKey, actionsHtml = "") {
  $("#pageTitle").textContent = t(titleKey);
  const a = $("#topbarActions"); a.innerHTML = actionsHtml; i18n.apply(a);
}

/* ---------- shared table helpers ---------- */
function emptyRow(cols, icon, key) {
  return `<tr><td colspan="${cols}"><div class="empty"><div class="ico">${icon}</div><div data-i18n="${key}">${t(key)}</div></div></td></tr>`;
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
  } catch (err) {
    $("#view").innerHTML = `<div class="empty"><div class="ico">⚠</div>${esc(err.message)}</div>`;
    stopRefresh();
  }
}

/* ===================================================================== */
/* DASHBOARD                                                              */
/* ===================================================================== */
async function viewDashboard() {
  setTopbar("dashboard.title", `<button class="btn" onclick="router()"><span>↻</span> <span data-i18n="common.refresh">${t("common.refresh")}</span></button>`);
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
  html += d.recentFailures.length ? backupTable(d.recentFailures, "dashboard.no_recent") : `<div class="empty"><div class="ico">✓</div>${t("dashboard.no_recent")}</div>`;

  $("#view").innerHTML = html;
  i18n.apply($("#view"));
}

function backupTable(rows, emptyKey) {
  if (!rows || !rows.length) return `<div class="table-wrap"><table><tbody>${emptyRow(6, "↯", emptyKey)}</tbody></table></div>`;
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
  setTopbar("hosts.title", `<button class="btn primary" onclick="hostForm()"><span>+</span> <span data-i18n="hosts.add">${t("hosts.add")}</span></button>`);
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
  if (!hosts.length) html += emptyRow(7, "🖥", "hosts.empty");
  else html += hosts.map(h => `<tr>
    <td><strong>${esc(h.name)}</strong><div class="cell-mono muted">${esc((h.useHttps ? "https" : "http") + "://" + h.ipOrFqdn + ":" + h.port)}</div></td>
    <td class="cell-dim">${esc(h.notes || "—")}</td>
    <td>${statusBadge(h.status)}</td>
    <td class="cell-mono">${esc(h.agentVersion || "—")}</td>
    <td class="cell-mono">${h.vmCount}</td>
    <td class="cell-mono">${fmtRelative(h.lastSeenAt)}</td>
    <td class="t-actions">
      <button class="btn sm" onclick="testHost(${h.id})" title="${esc(t("hosts.test_connection"))}">🔌</button>
      <button class="btn sm" onclick="syncHost(${h.id})" title="${esc(t("hosts.sync_vms"))}">⤓</button>
      <button class="btn sm" onclick="hostForm(${h.id})" title="${esc(t("common.edit"))}">✎</button>
      <button class="btn sm danger" onclick="delHost(${h.id}, '${esc(h.name)}')" title="${esc(t("common.delete"))}">🗑</button>
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
    if (id) { await api.put(`/api/hosts/${id}`, payload); toast(t("toast.success"), "ok"); }
    else { await api.post("/api/hosts", payload); toast(t("toast.created"), "ok"); }
    closeModal(); router();
  } catch (e) { toast(e.message, "err"); }
}

async function delHost(id, name) {
  if (!confirm(`${t("common.confirm_delete")}\n${name}`)) return;
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
/* VIRTUAL MACHINES                                                       */
/* ===================================================================== */
async function viewVms() {
  setTopbar("vms.title", "");
  const [hosts, vms] = await Promise.all([api.get("/api/hosts"), api.get("/api/vms")]);
  const hostOpts = [`<option value="">${t("common.all")}</option>`]
    .concat(hosts.map(h => `<option value="${h.id}">${esc(h.name)}</option>`)).join("");
  let html = `<div class="toolbar">
    <select id="vmHostFilter" onchange="router()" class="grow" style="max-width:280px">${hostOpts}</select>
    <span class="muted" id="vmCount"></span>
  </div>`;
  const fh = $("#vmHostFilter"); const hostId = fh ? fh.value : "";
  const filtered = hostId ? vms.filter(v => v.hostId == hostId) : vms;
  html += `<div class="table-wrap"><table>
    <thead><tr>
      <th data-i18n="common.name">${t("common.name")}</th>
      <th data-i18n="vms.host">${t("vms.host")}</th>
      <th data-i18n="vms.state">${t("vms.state")}</th>
      <th data-i18n="vms.disk">${t("vms.disk")}</th>
      <th data-i18n="vms.last_backup">${t("vms.last_backup")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!filtered.length) html += emptyRow(6, "◈", "vms.empty");
  else html += filtered.map(v => `<tr>
    <td><strong>${esc(v.name)}</strong><div class="cell-mono muted">${esc(v.externalId)}</div></td>
    <td class="cell-dim">${esc(v.hostName)}</td>
    <td>${esc(v.state)}</td>
    <td class="cell-mono">${fmtBytes(v.diskSizeBytes)}</td>
    <td>${v.lastBackupStatus ? `${statusBadge(v.lastBackupStatus)}<div class="cell-mono">${fmtRelative(v.lastBackupAt)}</div>` : `<span class="muted">${t("vms.no_backup")}</span>`}</td>
    <td class="t-actions"><button class="btn sm primary" onclick="backupVm(${v.hostId},${v.id},'${esc(v.name)}')" data-i18n="vms.backup_now">${t("vms.backup_now")}</button></td>
  </tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
  $("#vmCount").textContent = `${filtered.length}/${vms.length}`;
  if (hostId) $("#vmHostFilter").value = hostId;
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
  setTopbar("storages.title", `<button class="btn primary" onclick="storageForm()"><span>+</span> <span data-i18n="storages.add">${t("storages.add")}</span></button>`);
  const list = await api.get("/api/storages");
  let html = `<div class="table-wrap"><table>
    <thead><tr>
      <th data-i18n="common.name">${t("common.name")}</th>
      <th data-i18n="storages.type">${t("storages.type")}</th>
      <th data-i18n="storages.path">${t("storages.path")}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!list.length) html += emptyRow(4, "⛁", "storages.empty");
  else html += list.map(s => `<tr>
    <td><strong>${esc(s.name)}</strong></td>
    <td>${esc(t(`storages.types.${s.type}`))}</td>
    <td class="cell-mono">${esc(s.path)}</td>
    <td class="t-actions">
      <button class="btn sm" onclick="storageForm(${s.id})">✎</button>
      <button class="btn sm danger" onclick="delStorage(${s.id},'${esc(s.name)}')">🗑</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function storageForm(id = null) {
  let s = {};
  if (id) s = await api.get(`/api/storages`).then(l => l.find(x => x.id === id));
  const body = `<form id="sf" class="form-grid">
    <div class="field"><label data-i18n="common.name">${t("common.name")}</label><input name="name" value="${esc(s.name || "")}" required /></div>
    <div class="field"><label data-i18n="storages.type">${t("storages.type")}</label><select name="type">
      <option value="local_path" ${s.type === "local_path" ? "selected" : ""}>${t("storages.types.local_path")}</option>
      <option value="smb" ${s.type === "smb" ? "selected" : ""}>${t("storages.types.smb")}</option></select></div>
    <div class="field full"><label data-i18n="storages.path">${t("storages.path")}</label><input name="path" value="${esc(s.path || "")}" required /></div>
    <div class="field full"><label data-i18n="storages.notes">${t("storages.notes")}</label><textarea name="notes">${esc(s.notes || "")}</textarea></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveStorage(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal("storages.add", body, foot);
}

async function saveStorage(id) {
  const f = $("#sf"); const fd = new FormData(f);
  const payload = { name: fd.get("name"), type: fd.get("type"), path: fd.get("path"), notes: fd.get("notes") || null };
  try {
    if (id) { await api.put(`/api/storages/${id}`, payload); toast(t("toast.success"), "ok"); }
    else { await api.post("/api/storages", payload); toast(t("toast.created"), "ok"); }
    closeModal(); router();
  } catch (e) { toast(e.message, "err"); }
}

async function delStorage(id, name) {
  if (!confirm(`${t("common.confirm_delete")}\n${name}`)) return;
  try { await api.del(`/api/storages/${id}`); toast(t("toast.deleted"), "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* JOBS                                                                   */
/* ===================================================================== */
async function viewJobs() {
  setTopbar("jobs.title", `<button class="btn primary" onclick="jobForm()"><span>+</span> <span data-i18n="jobs.add">${t("jobs.add")}</span></button>`);
  const [jobs, hosts, vms, storages] = await Promise.all([
    api.get("/api/jobs"), api.get("/api/hosts"), api.get("/api/vms"), api.get("/api/storages")]);
  const vmById = Object.fromEntries(vms.map(v => [v.id, v]));
  let html = `<div class="table-wrap"><table>
    <thead><tr>
      <th data-i18n="jobs.name">${t("jobs.name")}</th>
      <th data-i18n="jobs.vm">${t("jobs.vm")}</th>
      <th data-i18n="jobs.type">${t("jobs.type")}</th>
      <th data-i18n="jobs.schedule_cron">${t("jobs.schedule_cron")}</th>
      <th data-i18n="jobs.last_run">${t("jobs.last_run")}</th>
      <th data-i18n="common.enabled">${""}</th>
      <th data-i18n="common.actions" class="t-actions">${t("common.actions")}</th>
    </tr></thead><tbody>`;
  if (!jobs.length) html += emptyRow(7, "⏱", "jobs.empty");
  else html += jobs.map(j => `<tr>
    <td><strong>${esc(j.name)}</strong><div class="cell-mono muted">${esc(j.vmName)} @ ${esc(j.hostName)}</div></td>
    <td class="cell-dim">${esc(j.storageName)}</td>
    <td>${esc(t(`jobs.types.${j.type}`))}</td>
    <td class="cell-mono">${j.cronSchedule ? esc(j.cronSchedule) : `<span class="muted">${t("jobs.manual_only")}</span>`}</td>
    <td class="cell-mono">${fmtRelative(j.lastRunAt)}</td>
    <td>${j.enabled ? `<span class="badge online"><span class="dot"></span>on</span>` : `<span class="badge offline"><span class="dot"></span>off</span>`}</td>
    <td class="t-actions">
      <button class="btn sm primary" onclick="runJob(${j.id})" data-i18n="common.run_now">${t("common.run_now")}</button>
      <button class="btn sm" onclick="jobForm(${j.id})">✎</button>
      <button class="btn sm danger" onclick="delJob(${j.id},'${esc(j.name)}')">🗑</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function jobForm(id = null) {
  const [hosts, vms, storages] = await Promise.all([api.get("/api/hosts"), api.get("/api/vms"), api.get("/api/storages")]);
  let j = {};
  if (id) j = await api.get("/api/jobs").then(l => l.find(x => x.id === id));
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
    <div class="field"><label data-i18n="jobs.schedule_cron">${t("jobs.schedule_cron")}</label><input name="cronSchedule" value="${esc(j.cronSchedule || "")}" placeholder="0 0 * * *" /></div>
    <div class="field"><label data-i18n="jobs.retention_days">${t("jobs.retention_days")}</label><input name="retentionDays" type="number" value="${j.retentionDays ?? 7}" /></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="enabled" ${j.enabled !== false ? "checked" : ""}/> <span data-i18n="jobs.enabled">${t("jobs.enabled")}</span></label></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="saveJob(${id || "null"})"><span data-i18n="common.save">${t("common.save")}</span></button>`;
  openModal("jobs.add", body, foot);
  filterVmOptions();
}
function filterVmOptions() {
  const host = $("#jfHost").value;
  $$("#jfVm option").forEach(o => {
    const match = !host || o.dataset.host == host;
    o.hidden = !match;
  });
  const firstVisible = $$("#jfVm option").find(o => !o.hidden);
  if (firstVisible) firstVisible.selected = true;
}
async function saveJob(id) {
  const f = $("#jf"); const fd = new FormData(f);
  const payload = {
    name: fd.get("name"), hostId: Number(fd.get("hostId")), vmId: Number(fd.get("vmId")),
    storageId: Number(fd.get("storageId")), type: fd.get("type"),
    cronSchedule: fd.get("cronSchedule") || "", retentionDays: Number(fd.get("retentionDays")),
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
  if (!confirm(`${t("common.confirm_delete")}\n${name}`)) return;
  try { await api.del(`/api/jobs/${id}`); toast(t("toast.deleted"), "ok"); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* BACKUPS (history)                                                      */
/* ===================================================================== */
async function viewBackups() {
  setTopbar("backups.title", `<button class="btn" onclick="router()"><span>↻</span> <span data-i18n="common.refresh">${t("common.refresh")}</span></button>`);
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
  if (!rows.length) html += emptyRow(9, "↯", "backups.empty");
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
      <button class="btn sm" onclick="verifyBackup(${r.id})" title="${esc(t("common.verify"))}">✓</button>
      <button class="btn sm" onclick="restoreFromBackup(${r.id})" title="${esc(t("common.restore"))}" ${r.status !== "succeeded" ? "disabled" : ""}>⟲</button>
    </td></tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function verifyBackup(id) {
  try { await api.post(`/api/backups/${id}/verify`); toast(t("toast.queued"), "ok"); location.hash = "#verifications"; router(); }
  catch (e) { toast(e.message, "err"); }
}

async function restoreFromBackup(backupId) {
  const b = await api.get(`/api/backups/${backupId}`);
  const hosts = await api.get("/api/hosts");
  const hOpts = hosts.map(h => `<option value="${h.id}" ${h.id === b.hostId ? "selected" : ""}>${esc(h.name)}</option>`).join("");
  const body = `<form id="rf" class="form-grid">
    <div class="field full"><div class="warn-box" data-i18n="restore.safety_warning">${t("restore.safety_warning")}</div></div>
    <div class="field full"><label data-i18n="restore.restore_point">${t("restore.restore_point")}</label><input name="restorePointPath" value="${esc(b.resultPath || "")}" required /></div>
    <div class="field"><label data-i18n="restore.target_host">${t("restore.target_host")}</label><select name="targetHostId" required>${hOpts}</select></div>
    <div class="field"><label data-i18n="restore.new_name">${t("restore.new_name")}</label><input name="newName" required value="${esc(b.vmName)}-restored" /></div>
    <div class="field full"><label data-i18n="restore.destination">${t("restore.destination")}</label><input name="destination" required /></div>
    <div class="field"><label data-i18n="restore.target_backup_id">${t("restore.target_backup_id")}</label><input name="targetBackupId" placeholder="inc-0001" /></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="overwriteExisting" /> <span data-i18n="restore.overwrite">${t("restore.overwrite")}</span></label></div>
    <input type="hidden" name="sourceHostId" value="${b.hostId}" />
    <input type="hidden" name="backupRunId" value="${b.id}" />
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitRestore()"><span data-i18n="common.restore">${t("common.restore")}</span></button>`;
  openModal("restore.new", body, foot);
}

/* ===================================================================== */
/* VERIFICATIONS                                                          */
/* ===================================================================== */
async function viewVerifications() {
  setTopbar("verifications.title", `<button class="btn primary" onclick="verifyForm()"><span>+</span> <span data-i18n="verifications.new">${t("verifications.new")}</span></button>`);
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
  if (!rows.length) html += emptyRow(6, "✓", "verifications.empty");
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
  const hosts = await api.get("/api/hosts");
  if (!hosts.length) { toast("Add a host first", "err"); location.hash = "#hosts"; return; }
  const hOpts = hosts.map(h => `<option value="${h.id}">${esc(h.name)}</option>`).join("");
  const body = `<form id="vf" class="form-grid">
    <div class="field"><label data-i18n="common.host">${t("common.host")}</label><select name="hostId" required>${hOpts}</select></div>
    <div class="field"><label data-i18n="verifications.kind">${t("verifications.kind")}</label><select name="kind">
      <option value="chain">${t("verifications.kinds.chain")}</option>
      <option value="restore">${t("verifications.kinds.restore")}</option></select></div>
    <div class="field full"><label data-i18n="verifications.target_path">${t("verifications.target_path")}</label><input name="targetPath" required /></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitVerify()"><span data-i18n="common.verify">${t("common.verify")}</span></button>`;
  openModal("verifications.new", body, foot);
}
async function submitVerify() {
  const f = $("#vf"); const fd = new FormData(f);
  try { await api.post("/api/verify", { hostId: Number(fd.get("hostId")), kind: fd.get("kind"), targetPath: fd.get("targetPath") }); toast(t("toast.queued"), "ok"); closeModal(); router(); }
  catch (e) { toast(e.message, "err"); }
}

/* ===================================================================== */
/* RESTORES                                                               */
/* ===================================================================== */
async function viewRestores() {
  setTopbar("restore.title", `<button class="btn primary" onclick="restoreForm()"><span>+</span> <span data-i18n="restore.new">${t("restore.new")}</span></button>`);
  const rows = await api.get("/api/restores");
  let html = `<div class="table-wrap scroll-x"><table>
    <thead><tr>
      <th data-i18n="common.status">${t("common.status")}</th>
      <th data-i18n="restore.target_host">${t("restore.target_host")}</th>
      <th data-i18n="restore.new_name">${t("restore.new_name")}</th>
      <th data-i18n="restore.destination">${t("restore.destination")}</th>
      <th data-i18n="backups.completed">${t("backups.completed")}</th>
      <th data-i18n="backups.error">${t("backups.error")}</th>
    </tr></thead><tbody>`;
  if (!rows.length) html += emptyRow(6, "⟲", "restore.empty");
  else html += rows.map(r => `<tr>
    <td>${statusBadge(r.status)}</td>
    <td class="cell-dim">${esc(r.targetHostName)}</td>
    <td><strong>${esc(r.newName)}</strong></td>
    <td class="cell-mono">${esc(r.destination)}</td>
    <td class="cell-mono">${fmtRelative(r.completedAt || r.startedAt || r.queuedAt)}</td>
    <td class="cell-dim" style="max-width:240px;overflow:hidden;text-overflow:ellipsis" title="${esc(r.error || "")}">${esc(r.error || "")}</td>
  </tr>`).join("");
  html += `</tbody></table></div>`;
  $("#view").innerHTML = html; i18n.apply($("#view"));
}

async function restoreForm() {
  const hosts = await api.get("/api/hosts");
  if (!hosts.length) { toast("Add a host first", "err"); location.hash = "#hosts"; return; }
  const hOpts = hosts.map(h => `<option value="${h.id}">${esc(h.name)}</option>`).join("");
  const body = `<form id="rf" class="form-grid">
    <div class="field full"><div class="warn-box" data-i18n="restore.safety_warning">${t("restore.safety_warning")}</div></div>
    <div class="field"><label data-i18n="restore.source_host">${t("restore.source_host")}</label><select name="sourceHostId" required>${hOpts}</select></div>
    <div class="field"><label data-i18n="restore.target_host">${t("restore.target_host")}</label><select name="targetHostId" required>${hOpts}</select></div>
    <div class="field full"><label data-i18n="restore.restore_point">${t("restore.restore_point")}</label><input name="restorePointPath" required /></div>
    <div class="field full"><label data-i18n="restore.destination">${t("restore.destination")}</label><input name="destination" required /></div>
    <div class="field"><label data-i18n="restore.new_name">${t("restore.new_name")}</label><input name="newName" required /></div>
    <div class="field"><label data-i18n="restore.target_backup_id">${t("restore.target_backup_id")}</label><input name="targetBackupId" placeholder="inc-0001" /></div>
    <div class="field"><label>&nbsp;</label><label class="check"><input type="checkbox" name="overwriteExisting" /> <span data-i18n="restore.overwrite">${t("restore.overwrite")}</span></label></div>
  </form>`;
  const foot = `<button class="btn ghost" data-close data-i18n="common.cancel">${t("common.cancel")}</button>
    <button class="btn primary" onclick="submitRestore()"><span data-i18n="common.restore">${t("common.restore")}</span></button>`;
  openModal("restore.new", body, foot);
}

async function submitRestore() {
  const f = $("#rf"); const fd = new FormData(f);
  const overwrite = f.overwriteExisting.checked;
  if (overwrite && !confirm(t("restore.confirm"))) { return; }
  const payload = {
    sourceHostId: Number(fd.get("sourceHostId")), targetHostId: Number(fd.get("targetHostId")),
    restorePointPath: fd.get("restorePointPath"), destination: fd.get("destination"),
    newName: fd.get("newName") || "", targetBackupId: fd.get("targetBackupId") || null,
    overwriteExisting: overwrite,
    backupRunId: fd.get("backupRunId") ? Number(fd.get("backupRunId")) : null
  };
  try { await api.post("/api/restore", payload); toast(t("toast.queued"), "ok"); closeModal(); location.hash = "#restores"; router(); }
  catch (e) { toast(e.message, "err"); }
}

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
        <button class="btn primary" onclick="userForm()"><span>+</span> <span data-i18n="settings.add_user">${t("settings.add_user")}</span></button></div>
      <div id="usersTableWrap">${pageLoader()}</div>
    </div>`;
  } else {
    html += `<div class="card"><div class="empty"><div class="ico">🔒</div><div data-i18n="settings.admin_only">${t("settings.admin_only")}</div></div></div>`;
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
    if (!users.length) html += emptyRow(5, "👤", "common.empty");
    else html += users.map(u => `<tr>
      <td><strong>${esc(u.username)}</strong>${u.id === currentUser.id ? ` <span class="muted">(${t("settings.you")})</span>` : ""}</td>
      <td>${esc(t("roles." + u.role))}</td>
      <td>${u.enabled ? `<span class="badge online"><span class="dot"></span>${t("status.online")}</span>` : `<span class="badge offline"><span class="dot"></span>${t("status.offline")}</span>`}</td>
      <td class="cell-mono">${fmtRelative(u.lastLoginAt)}</td>
      <td class="t-actions">
        <button class="btn sm" onclick="userForm(${u.id})" title="${esc(t("common.edit"))}">✎</button>
        <button class="btn sm" onclick="resetPasswordForm(${u.id},'${esc(u.username)}')" title="${esc(t("common.reset_password"))}">🔑</button>
        <button class="btn sm danger" onclick="delUser(${u.id},'${esc(u.username)}')" title="${esc(t("common.delete"))}" ${u.id === currentUser.id ? "disabled" : ""}>🗑</button>
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
  if (!confirm(`${t("common.confirm_delete")}\n${username}`)) return;
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
