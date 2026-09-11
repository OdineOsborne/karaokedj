# VOXA cloud

Servizio su Vercel: donazioni PayPal → licenza istantanea (e, in futuro, pagina QR richieste canzoni).

Variabili d'ambiente (Vercel → Settings → Environment Variables):
- `VOXA_LICENSE_PRIVATE_KEY` — contenuto del file `voxa-license-private.pem` (PEM, va bene anche su una riga con `\n`)
- `PAYPAL_CLIENT_ID`, `PAYPAL_CLIENT_SECRET` — app REST PayPal (Live). `PAYPAL_ENV=sandbox` per le prove.
- `PAYPAL_CURRENCY` — default `EUR`
- `PAYPAL_WEBHOOK_ID` — (opzionale) id del webhook `PAYMENT.CAPTURE.COMPLETED` puntato a `/api/webhook`
- `UPSTASH_REDIS_REST_URL`, `UPSTASH_REDIS_REST_TOKEN` — Upstash Redis (marketplace Vercel) per salvare le licenze

Endpoint: `GET /dona?m=ID` pagina donazione · `POST /api/order` · `POST /api/capture` · `GET /api/license?m=ID` · `POST /api/webhook`.
