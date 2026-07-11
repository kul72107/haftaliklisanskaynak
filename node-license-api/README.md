# Modern Yedek Node License API

This is a Node.js version of the Modern Yedek manual license API. It has no npm dependencies and keeps the same HTTP contract used by the WPF app.

## Run

```bash
export MODERN_YEDEK_ADMIN_TOKEN="change-this-admin-token"
export MODERN_YEDEK_LICENSE_SALT="change-this-random-salt"
export MODERN_YEDEK_LICENSE_DB="./data/licenses.json"
export PORT="5088"
npm start
```

## Required public endpoints

- `GET /health`
- `GET /admin/keygen`
- `POST /admin/licenses`
- `POST /admin/licenses/:licenseKey/extend`
- `POST /admin/licenses/:licenseKey/cancel`
- `POST /license/activate`
- `POST /license/validate`

The WPF app embeds the public base URL and only asks the customer for a license key.

## Persistent data

`MODERN_YEDEK_LICENSE_DB` must point to a persistent file path. If that file is deleted, previously generated keys are lost.
