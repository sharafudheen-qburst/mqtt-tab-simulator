# Node.js MQTT Bridge (OpenSSL)

Bypasses Windows Schannel mTLS limitations by delegating `mqtts://` connections to Node.js.

## Prerequisites

- [Node.js](https://nodejs.org/) 18 or later on `PATH`, or set `NODE_BINARY` to the full path to `node.exe`.

## Setup

From this folder (`src/.../TabletSimulator/NodeBridge`):

```bash
npm install
```

After building the .NET app, dependencies must also exist next to the copied script:

```bash
cd bin/Debug/net8.0/NodeBridge
npm install
```

Or run `npm install` once in the source `NodeBridge` folder and copy `node_modules` to the output folder.

## CLI usage

Validate connection (PEM certs in current directory or pass paths):

```bash
node mqtt-bridge.js --action validate \
  --host 10.10.127.155 --port 31884 --client-id abcdef \
  --ca ca.crt --cert client.crt --key client.key
```

Publish (payload via stdin JSON recommended from .NET):

```bash
echo '{"action":"publish","host":"10.10.127.155","port":31884,"clientId":"abcdef","topic":"test/topic","payloadBase64":"aGVsbG8=","caFile":"ca.crt","certFile":"client.crt","keyFile":"client.key"}' | node mqtt-bridge.js --stdin
```

## Environment variables

| Variable | Description |
|----------|-------------|
| `MQTT_BRIDGE_HOST` | Broker hostname |
| `MQTT_BRIDGE_PORT` | Broker port |
| `MQTT_BRIDGE_CLIENT_ID` | MQTT client ID |
| `MQTT_BRIDGE_TOPIC` | Publish topic |
| `MQTT_BRIDGE_CA_FILE` | CA PEM path |
| `MQTT_BRIDGE_CERT_FILE` | Client cert PEM path |
| `MQTT_BRIDGE_KEY_FILE` | Client key PEM path |
| `MQTT_BRIDGE_ACTION` | `validate` or `publish` |
| `MQTT_BRIDGE_REJECT_UNAUTHORIZED` | `true` / `false` (default `false`) |
| `NODE_BINARY` | (.NET only) Path to node executable |

## Tablet Simulator

Enable **Use Node.js MQTT bridge (OpenSSL)** in Settings for environments where Schannel fails (`0x80090304`).

Requires **PEM** `client.crt` + `client.key` (same paths as MQTTX). PFX is not supported by the Node bridge.
