import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const LicenseState = {
  Unknown: 0,
  Active: 1,
  Trialing: 2,
  PastDue: 3,
  Expired: 4,
  Canceled: 5,
  Invalid: 6
};

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.PORT || process.env.MODERN_YEDEK_PORT || 5088);
const host = process.env.HOST || '0.0.0.0';
const adminToken = process.env.MODERN_YEDEK_ADMIN_TOKEN || 'dev-admin-token';
const hashSalt = process.env.MODERN_YEDEK_LICENSE_SALT || 'dev-license-salt-change-me';
const dbPath = process.env.MODERN_YEDEK_LICENSE_DB || path.join(__dirname, '..', 'data', 'licenses.json');

const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url || '/', `http://${request.headers.host || 'localhost'}`);
    const route = normalizePath(url.pathname);

    if (request.method === 'GET' && route === '/health') {
      return json(response, 200, {
        ok: true,
        service: 'ModernYedek.NodeLicenseApi',
        mode: 'manual-key'
      });
    }

    if (request.method === 'GET' && route === '/admin/keygen') {
      return html(response, 200, adminKeygenHtml());
    }

    if (request.method === 'POST' && route === '/admin/licenses') {
      if (!isAdmin(request)) return json(response, 401, { message: 'Unauthorized.' });
      const body = await readJson(request);
      const result = await createLicense(body);
      return json(response, 200, result);
    }

    const extendMatch = route.match(/^\/admin\/licenses\/([^/]+)\/extend$/);
    if (request.method === 'POST' && extendMatch) {
      if (!isAdmin(request)) return json(response, 401, { message: 'Unauthorized.' });
      const body = await readJson(request);
      const result = await extendLicense(decodeURIComponent(extendMatch[1]), body);
      return result ? json(response, 200, result) : json(response, 404, { message: 'License not found.' });
    }

    const cancelMatch = route.match(/^\/admin\/licenses\/([^/]+)\/cancel$/);
    if (request.method === 'POST' && cancelMatch) {
      if (!isAdmin(request)) return json(response, 401, { message: 'Unauthorized.' });
      const ok = await cancelLicense(decodeURIComponent(cancelMatch[1]));
      return ok ? json(response, 200, { message: 'License canceled.' }) : json(response, 404, { message: 'License not found.' });
    }

    if (request.method === 'POST' && route === '/license/activate') {
      const body = await readJson(request);
      const result = await activateLicense(body);
      return json(response, 200, result);
    }

    if (request.method === 'POST' && route === '/license/validate') {
      const body = await readJson(request);
      const result = await validateLicense(body);
      return json(response, 200, result);
    }

    return json(response, 404, { message: 'Not found.' });
  } catch (error) {
    return json(response, 500, {
      message: error instanceof Error ? error.message : 'Unexpected server error.'
    });
  }
});

server.listen(port, host, () => {
  console.log(`ModernYedek Node License API listening on http://${host}:${port}`);
  console.log(`License DB: ${dbPath}`);
});

async function createLicense(request) {
  return withDb(async (db) => {
    const licenseKey = generateLicenseKey();
    const now = new Date();
    const durationDays = Math.max(1, toInt(request.days, 7));
    const record = {
      licenseId: `lic_${crypto.randomUUID().replaceAll('-', '')}`,
      keyHash: hashKey(licenseKey),
      email: normalizeEmail(request.email || ''),
      plan: stringOrDefault(request.plan, 'weekly_pro'),
      status: 'active',
      paidUntil: addDays(now, durationDays).toISOString(),
      durationDays,
      startsOnActivation: request.startsOnActivation !== false,
      firstActivatedAt: null,
      activationLimit: Math.max(1, toInt(request.activationLimit, 1)),
      notes: String(request.notes || '').trim(),
      createdAt: now.toISOString(),
      updatedAt: now.toISOString(),
      activations: []
    };

    db.licenses.push(record);
    return toAdminResponse(record, licenseKey);
  });
}

async function extendLicense(licenseKey, request) {
  return withDb(async (db) => {
    const record = findByKey(db, licenseKey);
    if (!record) return null;

    const now = new Date();
    const days = Math.max(1, toInt(request.days, 7));
    if (record.startsOnActivation && !record.firstActivatedAt) {
      record.durationDays = Math.max(1, toInt(record.durationDays, 7)) + days;
      record.paidUntil = addDays(now, record.durationDays).toISOString();
    } else {
      const currentPaidUntil = parseDate(record.paidUntil) || now;
      const start = currentPaidUntil > now ? currentPaidUntil : now;
      record.paidUntil = addDays(start, days).toISOString();
    }

    record.status = 'active';
    record.updatedAt = now.toISOString();
    return toAdminResponse(record, licenseKey);
  });
}

async function cancelLicense(licenseKey) {
  return withDb(async (db) => {
    const record = findByKey(db, licenseKey);
    if (!record) return false;

    record.status = 'canceled';
    record.updatedAt = new Date().toISOString();
    return true;
  });
}

async function activateLicense(request) {
  return withDb(async (db) => {
    const record = findByKey(db, request.licenseKey || '');
    if (!record) return invalid('License key not found.');

    if (!emailMatches(record, request.email || '')) {
      return invalid('Email does not match this license.');
    }

    const machineId = normalizeMachineId(request.machineId || '');
    if (!machineId) return invalid('Machine id is empty.');

    if (isCanceled(record)) return buildAccess(record);

    let activation = record.activations.find((item) => item.machineId === machineId);
    const now = new Date();
    if (!activation) {
      if (record.activations.length >= Math.max(1, toInt(record.activationLimit, 1))) {
        return invalid('Activation limit reached.');
      }

      if (record.startsOnActivation && !record.firstActivatedAt) {
        record.firstActivatedAt = now.toISOString();
        record.paidUntil = addDays(now, Math.max(1, toInt(record.durationDays, 7))).toISOString();
      }

      activation = {
        machineId,
        activatedAt: now.toISOString(),
        lastSeenAt: now.toISOString()
      };
      record.activations.push(activation);
    } else {
      activation.lastSeenAt = now.toISOString();
    }

    record.updatedAt = now.toISOString();
    return buildAccess(record);
  });
}

async function validateLicense(request) {
  return withDb(async (db) => {
    const record = findByKey(db, request.licenseKey || '');
    if (!record) return invalid('License key not found.');

    if (!emailMatches(record, request.email || '')) {
      return invalid('Email does not match this license.');
    }

    const machineId = normalizeMachineId(request.machineId || '');
    const activation = record.activations.find((item) => item.machineId === machineId);
    if (!activation) return invalid('This machine is not activated.');

    activation.lastSeenAt = new Date().toISOString();
    record.updatedAt = new Date().toISOString();
    return buildAccess(record);
  });
}

function buildAccess(record) {
  const now = new Date();
  const paidUntil = parseDate(record.paidUntil) || now;

  if (isCanceled(record)) {
    return {
      isValid: false,
      state: LicenseState.Canceled,
      provider: 'manual',
      licenseId: record.licenseId,
      customerEmail: record.email,
      plan: record.plan,
      paidUntil: paidUntil.toISOString(),
      message: 'License is canceled.',
      activationLimit: Math.max(1, toInt(record.activationLimit, 1)),
      activationCount: record.activations.length
    };
  }

  if (now > paidUntil) {
    return {
      isValid: false,
      state: LicenseState.Expired,
      provider: 'manual',
      licenseId: record.licenseId,
      customerEmail: record.email,
      plan: record.plan,
      paidUntil: paidUntil.toISOString(),
      message: 'License expired.',
      activationLimit: Math.max(1, toInt(record.activationLimit, 1)),
      activationCount: record.activations.length
    };
  }

  const offlineUntil = minDate(addHours(now, 72), paidUntil);
  return {
    isValid: true,
    state: LicenseState.Active,
    provider: 'manual',
    licenseId: record.licenseId,
    customerEmail: record.email,
    plan: record.plan,
    paidUntil: paidUntil.toISOString(),
    offlineUntil: offlineUntil.toISOString(),
    message: 'License active.',
    activationLimit: Math.max(1, toInt(record.activationLimit, 1)),
    activationCount: record.activations.length
  };
}

function invalid(message) {
  return {
    isValid: false,
    state: LicenseState.Invalid,
    provider: 'manual',
    message
  };
}

async function withDb(action) {
  const db = await loadDb();
  const result = await action(db);
  await saveDb(db);
  return result;
}

async function loadDb() {
  try {
    const text = await fs.readFile(dbPath, 'utf8');
    const parsed = JSON.parse(text);
    return {
      licenses: Array.isArray(parsed.licenses) ? parsed.licenses : Array.isArray(parsed.Licenses) ? parsed.Licenses : []
    };
  } catch (error) {
    if (error && error.code === 'ENOENT') return { licenses: [] };
    throw error;
  }
}

async function saveDb(db) {
  await fs.mkdir(path.dirname(dbPath), { recursive: true });
  const tempPath = `${dbPath}.${process.pid}.tmp`;
  await fs.writeFile(tempPath, JSON.stringify(db, null, 2), 'utf8');
  await fs.rename(tempPath, dbPath);
}

function findByKey(db, licenseKey) {
  const keyHash = hashKey(licenseKey || '');
  return db.licenses.find((record) => record.keyHash === keyHash || record.KeyHash === keyHash) || null;
}

function toAdminResponse(record, licenseKey) {
  return {
    licenseId: record.licenseId,
    licenseKey,
    email: record.email,
    plan: record.plan,
    paidUntil: record.paidUntil,
    durationDays: record.durationDays,
    activationLimit: record.activationLimit,
    startsOnActivation: record.startsOnActivation,
    firstActivatedAt: record.firstActivatedAt
  };
}

function isAdmin(request) {
  return String(request.headers['x-admin-token'] || '') === adminToken;
}

function generateLicenseKey() {
  const hex = crypto.randomBytes(12).toString('hex').toUpperCase();
  const groups = [];
  for (let index = 0; index < 6; index += 1) {
    groups.push(hex.slice(index * 4, index * 4 + 4));
  }

  return `MY-${groups.join('-')}`;
}

function hashKey(licenseKey) {
  const normalized = normalizeLicenseKey(licenseKey);
  return crypto.createHash('sha256').update(`${hashSalt}|${normalized}`, 'utf8').digest('hex').toUpperCase();
}

function normalizeLicenseKey(value) {
  return String(value || '').trim().replaceAll('-', '').toUpperCase();
}

function normalizeEmail(value) {
  return String(value || '').trim().toLowerCase();
}

function normalizeMachineId(value) {
  return String(value || '').trim().toUpperCase();
}

function emailMatches(record, email) {
  return normalizeEmail(record.email) === normalizeEmail(email);
}

function isCanceled(record) {
  return String(record.status || '').toLowerCase() === 'canceled';
}

function stringOrDefault(value, fallback) {
  const text = String(value || '').trim();
  return text || fallback;
}

function toInt(value, fallback) {
  const parsed = Number.parseInt(String(value), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function parseDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

function addDays(date, days) {
  return new Date(date.getTime() + days * 24 * 60 * 60 * 1000);
}

function addHours(date, hours) {
  return new Date(date.getTime() + hours * 60 * 60 * 1000);
}

function minDate(left, right) {
  return left <= right ? left : right;
}

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const text = Buffer.concat(chunks).toString('utf8');
  if (!text.trim()) return {};
  return JSON.parse(text);
}

function json(response, statusCode, value) {
  const body = JSON.stringify(value);
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body)
  });
  response.end(body);
}

function html(response, statusCode, value) {
  response.writeHead(statusCode, {
    'Content-Type': 'text/html; charset=utf-8',
    'Content-Length': Buffer.byteLength(value)
  });
  response.end(value);
}

function normalizePath(value) {
  const route = value.replace(/\/+$/, '');
  return route || '/';
}

function adminKeygenHtml() {
  return `<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Modern Yedek Key Uret</title>
  <style>
    :root { color-scheme: light; --ink:#152033; --muted:#657085; --line:#d8dee8; --accent:#1f7a6d; --bg:#f4f7fb; }
    * { box-sizing: border-box; }
    body { margin:0; background:var(--bg); color:var(--ink); font-family: Segoe UI, Arial, sans-serif; }
    main { max-width:560px; margin:0 auto; padding:24px 16px 40px; }
    h1 { margin:0 0 6px; font-size:26px; }
    p { color:var(--muted); line-height:1.45; }
    .panel { background:white; border:1px solid var(--line); border-radius:8px; padding:18px; box-shadow:0 8px 28px rgba(21,32,51,.08); }
    label { display:block; margin-top:14px; font-weight:600; font-size:13px; }
    input, textarea { width:100%; height:42px; margin-top:6px; padding:9px 10px; border:1px solid var(--line); border-radius:7px; font:inherit; }
    textarea { height:72px; resize:vertical; }
    button { width:100%; height:44px; margin-top:18px; border:0; border-radius:7px; background:var(--accent); color:white; font-weight:700; font-size:15px; }
    button.secondary { background:#edf2f7; color:var(--ink); border:1px solid var(--line); }
    pre { white-space:pre-wrap; word-break:break-word; background:#101828; color:white; padding:14px; border-radius:8px; min-height:72px; }
    .row { display:grid; grid-template-columns:1fr 1fr; gap:10px; }
    .ok { color:#0b7a42; }
    .error { color:#b42318; }
  </style>
</head>
<body>
  <main>
    <h1>Modern Yedek Key Uret</h1>
    <p>Bu panel tek kullanimlik manuel lisans key uretir. Key ilk aktivasyonda girilen bilgisayara baglanir ve sure o anda baslar.</p>
    <section class="panel">
      <label>Admin token</label>
      <input id="token" type="password" autocomplete="off" placeholder="MODERN_YEDEK_ADMIN_TOKEN">
      <label>Musteri email</label>
      <input id="email" type="email" placeholder="musteri@example.com">
      <div class="row">
        <div><label>Sure</label><input id="days" type="number" min="1" value="7"></div>
        <div><label>Cihaz limiti</label><input id="limit" type="number" min="1" value="1"></div>
      </div>
      <label>Plan</label>
      <input id="plan" value="weekly_pro">
      <label>Not</label>
      <textarea id="notes" placeholder="Opsiyonel"></textarea>
      <button id="generate">Yeni key olustur</button>
      <button class="secondary" id="copy" type="button">Key kopyala</button>
      <p id="status"></p>
      <pre id="result">Key burada gorunecek.</pre>
    </section>
  </main>
  <script>
    const result = document.getElementById('result');
    const status = document.getElementById('status');
    let lastKey = '';
    document.getElementById('generate').addEventListener('click', async () => {
      status.textContent = 'Key uretiliyor...';
      status.className = '';
      result.textContent = '';
      lastKey = '';
      const token = document.getElementById('token').value.trim();
      const payload = {
        email: document.getElementById('email').value.trim(),
        days: Number(document.getElementById('days').value || 7),
        activationLimit: Number(document.getElementById('limit').value || 1),
        plan: document.getElementById('plan').value.trim() || 'weekly_pro',
        startsOnActivation: true,
        notes: document.getElementById('notes').value.trim()
      };
      try {
        const response = await fetch('/admin/licenses', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Admin-Token': token },
          body: JSON.stringify(payload)
        });
        if (!response.ok) throw new Error('HTTP ' + response.status);
        const data = await response.json();
        lastKey = data.licenseKey;
        status.textContent = 'Key hazir.';
        status.className = 'ok';
        result.textContent =
          'KEY: ' + data.licenseKey + '\\n' +
          'Email: ' + data.email + '\\n' +
          'Sure: ' + data.durationDays + ' gun\\n' +
          'Cihaz: 0/' + data.activationLimit + '\\n' +
          'Sure baslangici: Ilk aktivasyon';
      } catch (error) {
        status.textContent = 'Key uretilemedi: ' + error.message;
        status.className = 'error';
      }
    });
    document.getElementById('copy').addEventListener('click', async () => {
      if (!lastKey) return;
      await navigator.clipboard.writeText(lastKey);
      status.textContent = 'Key kopyalandi.';
      status.className = 'ok';
    });
  </script>
</body>
</html>`;
}
