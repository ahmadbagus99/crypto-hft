# Seqra Quant notifications with Bark

Bark is the default free notification path for Seqra Quant while the iOS app is
signed with an Apple Personal Team. The backend sends notifications for:

- a newly opened position;
- take-profit execution;
- stop-loss execution;
- trailing-stop execution;
- automatic or manual position close.

The native APNs integration remains available separately and can stay disabled.
With Bark, the Seqra Quant app itself does not need the Push Notifications
capability because the notification is delivered by the Bark iOS app.

## Fastest setup using the public Bark server

1. Install Bark on the iPhone and allow notifications.
2. Open Bark and copy the generated test URL. It looks like
   `https://api.day.app/DEVICE_KEY/Test`.
3. Keep only the server and device key, then set the deployment `.env`:

```dotenv
BARK_ENABLED=true
BARK_SERVER_URL=https://api.day.app
BARK_DEVICE_KEY=DEVICE_KEY
```

Alternatively, set the exact push endpoint directly. This value may contain a
secret device key, so never commit it:

```dotenv
BARK_ENABLED=true
BARK_PUSH_URL=https://api.day.app/DEVICE_KEY
```

`BARK_PUSH_URL` takes precedence over `BARK_SERVER_URL` and `BARK_DEVICE_KEY`.

Optional presentation settings:

```dotenv
BARK_SOUND=minuet
BARK_GROUP=seqra-quant
BARK_LEVEL=timeSensitive
BARK_ICON_URL=
BARK_OPEN_URL=https://trading.seqra.space
```

Then rebuild and restart the API with the existing Docker Compose deployment
workflow. Do not put the Bark key in `appsettings.json` or source control.

## Self-hosted Bark server

For a self-hosted server, point `BARK_SERVER_URL` to its HTTPS address and use the
device key created after adding that server in the Bark iOS app:

```dotenv
BARK_ENABLED=true
BARK_SERVER_URL=https://bark.example.com
BARK_DEVICE_KEY=DEVICE_KEY_FROM_SELF_HOSTED_SERVER
```

If the API container accesses Bark only through the Docker network, an internal URL
such as `http://bark:8080` can be used. The iPhone still needs a publicly reachable
HTTPS address to register with and use that Bark server.

## Disable Bark

Set `BARK_ENABLED=false`. Trading continues normally; failed or unavailable push
delivery is logged and never blocks position processing.
