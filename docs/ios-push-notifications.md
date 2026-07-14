# Seqra Quant iOS push notifications

The backend now supports APNs notifications for:

- a newly opened position;
- take-profit execution;
- stop-loss execution;
- trailing-stop execution;
- automatic or manual position close.

The iOS app registers its APNs device token through `POST /api/notifications/devices`.
Tokens are stored in PostgreSQL in `trading."PushDevices"`. Invalid or unregistered
tokens are disabled automatically when APNs rejects them.

## Apple account requirement

Remote push notifications require a paid Apple Developer Program team. Personal
development teams cannot sign an app with the `aps-environment` entitlement.

After the membership is active:

1. Enable **Push Notifications** for App ID `com.ahmadbagus.cryptoHFT` in the Apple
   Developer portal and in Xcode Signing & Capabilities.
2. Create an APNs authentication key and download the `.p8` file. Record its Key ID
   and the Apple Developer Team ID. The key can be downloaded only once.
3. For device testing, add `CryptoHFT/CryptoHFT.entitlements` to the Debug target's
   `CODE_SIGN_ENTITLEMENTS` and add `PUSH_NOTIFICATIONS` to Debug's Swift compilation
   conditions. Release is already configured for both.
4. Regenerate the provisioning profile and reinstall the app on the iPhone.

## Backend environment

Convert the `.p8` file to a single-line Base64 value and set these variables in the
deployment `.env` file. Never commit the key or its Base64 value.

```dotenv
APNS_ENABLED=true
APNS_TEAM_ID=YOUR_TEAM_ID
APNS_KEY_ID=YOUR_KEY_ID
APNS_BUNDLE_ID=com.ahmadbagus.cryptoHFT
APNS_PRIVATE_KEY_BASE64=BASE64_ENCODED_P8_CONTENT
```

`docker-compose.prod.yml` forwards these values into the API container. The same APNs
key works with sandbox and production; each registered device records the correct APNs
environment automatically.

## Manual deployment

Build and deploy the updated API using the existing Docker Compose workflow. On first
startup, the API creates the `trading."PushDevices"` table idempotently. No destructive
database migration is required.

Once the updated backend is live, launch the newly signed iOS app once and allow
notifications. That launch registers the current APNs device token with the backend.
Afterward, APNs can deliver trading notifications even when the app is not running.
