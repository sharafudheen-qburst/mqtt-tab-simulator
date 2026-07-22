#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

process.on('uncaughtException', (err) => {
  console.error('CRITICAL UNCAUGHT EXCEPTION:', err.message);
  console.error(err.stack);
  process.exit(1);
});

process.on('unhandledRejection', (reason) => {
  console.error('UNHANDLED REJECTION:', reason);
  process.exit(1);
});

function logInfo(message) {
  process.stderr.write(`${message}\n`);
}

function emitEvent(event) {
  process.stdout.write(`${JSON.stringify(event)}\n`);
}

function fail(message) {
  process.stderr.write(`ERROR: ${message}\n`);
  process.exit(1);
}

function parseArgs(argv) {
  const out = {};
  for (let i = 2; i < argv.length; i++) {
    const arg = argv[i];
    if (!arg.startsWith('--')) continue;
    const key = arg.slice(2);
    const next = argv[i + 1];
    if (!next || next.startsWith('--')) {
      out[key] = true;
    } else {
      out[key] = next;
      i++;
    }
  }
  return out;
}

/**
 * Reads the first stdin JSON document (one line, or entire stream until EOF).
 * Leaves stdin open so listen mode can receive a later {"cmd":"stop"} line.
 */
function readFirstStdinJson() {
  return new Promise((resolve) => {
    if (process.stdin.isTTY) {
      resolve(null);
      return;
    }

    process.stdin.setEncoding('utf8');
    let buffer = '';

    const finish = (raw) => {
      process.stdin.removeListener('data', onData);
      process.stdin.removeListener('end', onEnd);
      const text = String(raw || '').trim();
      if (!text) {
        resolve(null);
        return;
      }
      try {
        resolve(JSON.parse(text));
      } catch (err) {
        fail(`Invalid stdin JSON: ${err.message}`);
      }
    };

    const onData = (chunk) => {
      buffer += chunk;
      const nl = buffer.indexOf('\n');
      if (nl >= 0) {
        finish(buffer.slice(0, nl));
      }
    };

    const onEnd = () => finish(buffer);

    process.stdin.on('data', onData);
    process.stdin.on('end', onEnd);
  });
}

function watchStdinForStop(onStop) {
  if (process.stdin.isTTY || process.stdin.readableEnded) {
    return;
  }

  process.stdin.setEncoding('utf8');
  let buffer = '';
  process.stdin.on('data', (chunk) => {
    buffer += chunk;
    let nl;
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl).trim();
      buffer = buffer.slice(nl + 1);
      if (!line) continue;
      try {
        const msg = JSON.parse(line);
        if (msg && (msg.cmd === 'stop' || msg.action === 'stop')) {
          onStop();
        }
      } catch {
        if (line.toLowerCase() === 'stop') {
          onStop();
        }
      }
    }
  });
}

function resolvePath(maybePath, label, required) {
  if (!maybePath) {
    if (required) {
      fail(`${label} path is required`);
    }
    return null;
  }

  const resolved = path.isAbsolute(maybePath)
    ? maybePath
    : path.resolve(process.cwd(), maybePath);

  if (!fs.existsSync(resolved)) {
    fail(`${label} not found: ${resolved}`);
  }

  return resolved;
}

function buildConfig(cli, stdin) {
  const src = { ...(stdin || {}), ...cli };

  const host = src.host || src.brokerHost || process.env.MQTT_BRIDGE_HOST;
  const port = Number(src.port || src.brokerPort || process.env.MQTT_BRIDGE_PORT || 8883);
  const clientId = src.clientId || process.env.MQTT_BRIDGE_CLIENT_ID || `node-bridge-${process.pid}`;
  const action = String(src.action || process.env.MQTT_BRIDGE_ACTION || 'validate').toLowerCase();
  const topic = src.topic || process.env.MQTT_BRIDGE_TOPIC || '';
  const timeoutMs = Number(src.timeoutMs || process.env.MQTT_BRIDGE_TIMEOUT_MS || 16000);
  const rejectUnauthorized = String(
    src.rejectUnauthorized ?? process.env.MQTT_BRIDGE_REJECT_UNAUTHORIZED ?? 'false'
  ).toLowerCase() === 'true';

  const caFile = src.caFile || src.ca || process.env.MQTT_BRIDGE_CA_FILE;
  const certFile = src.certFile || src.cert || process.env.MQTT_BRIDGE_CERT_FILE;
  const keyFile = src.keyFile || src.key || process.env.MQTT_BRIDGE_KEY_FILE;
  const topics = Array.isArray(src.topics) ? src.topics : [];

  if (!host) {
    fail('Broker host is required');
  }

  if (action === 'publish' && !topic) {
    fail('Topic is required for publish action');
  }

  if (action === 'listen' && topics.length === 0) {
    fail('At least one subscription topic is required for listen action');
  }

  const caPath = resolvePath(caFile, 'CA file', true);
  const certPath = resolvePath(certFile, 'Client certificate', true);
  const keyPath = resolvePath(keyFile, 'Client key', true);

  let payload = Buffer.alloc(0);
  if (src.payloadBase64) {
    payload = Buffer.from(src.payloadBase64, 'base64');
  } else if (typeof src.payload === 'string') {
    payload = Buffer.from(src.payload, 'utf8');
  }

  return {
    host,
    port,
    clientId,
    action,
    topic,
    timeoutMs,
    rejectUnauthorized,
    caPath,
    certPath,
    keyPath,
    payload,
    username: src.username || process.env.MQTT_BRIDGE_USERNAME || '',
    password: src.password || process.env.MQTT_BRIDGE_PASSWORD || '',
    topics,
  };
}

function loadMqtt() {
  try {
    return require('mqtt');
  } catch {
    fail('Failed to load mqtt library. Run: npm install (in NodeBridge folder)');
  }
}

function createClient(config, persistent) {
  const mqtt = loadMqtt();

  logInfo('Checking certificate files...');
  logInfo(`CA: ${config.caPath}`);
  logInfo(`Cert: ${config.certPath}`);
  logInfo(`Key: ${config.keyPath}`);

  const options = {
    host: config.host,
    port: config.port,
    protocol: 'mqtts',
    clientId: config.clientId,
    ca: fs.readFileSync(config.caPath),
    cert: fs.readFileSync(config.certPath),
    key: fs.readFileSync(config.keyPath),
    rejectUnauthorized: config.rejectUnauthorized,
    reconnectPeriod: persistent ? 4000 : 0,
    connectTimeout: config.timeoutMs,
    clean: true,
  };

  if (config.username) {
    options.username = config.username;
    options.password = config.password || '';
  }

  logInfo(`Attempting connection to mqtts://${config.host}:${config.port} as ${config.clientId}...`);
  return mqtt.connect(options);
}

function runValidate(config) {
  return new Promise((resolve, reject) => {
    const client = createClient(config);
    let settled = false;

    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      fn(value);
    };

    client.on('connect', () => {
      logInfo('SUCCESS: Connected to MQTT Broker!');
      client.end(true, {}, () => finish(resolve));
    });

    client.on('error', (err) => {
      finish(reject, new Error(`MQTT client error: ${err.message}`));
    });

    const timer = setTimeout(() => {
      client.end(true);
      finish(reject, new Error(`Timed out after ${config.timeoutMs}ms with no response from broker`));
    }, config.timeoutMs + 1000);
  });
}

function runPublish(config) {
  return new Promise((resolve, reject) => {
    const client = createClient(config);
    let settled = false;

    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      fn(value);
    };

    client.on('connect', () => {
      logInfo(`Publishing ${config.payload.length} bytes to ${config.topic}`);
      client.publish(config.topic, config.payload, { qos: 1 }, (err) => {
        if (err) {
          finish(reject, err);
          return;
        }
        logInfo('Publish acknowledged');
        client.end(true, {}, () => finish(resolve));
      });
    });

    client.on('error', (err) => {
      finish(reject, new Error(`MQTT client error: ${err.message}`));
    });

    const timer = setTimeout(() => {
      client.end(true);
      finish(reject, new Error(`Timed out after ${config.timeoutMs}ms`));
    }, config.timeoutMs + 1000);
  });
}

function stopListenClient(client, topics, done) {
  let finished = false;
  const finish = () => {
    if (finished) return;
    finished = true;
    done();
  };

  const endClient = () => {
    try {
      client.end(true, {}, finish);
    } catch {
      finish();
    }
  };

  const list = Array.isArray(topics) ? topics.filter(Boolean) : [];
  if (list.length === 0) {
    endClient();
    return;
  }

  try {
    client.unsubscribe(list, () => endClient());
  } catch {
    endClient();
  }
}

function runListen(config) {
  return new Promise((resolve, reject) => {
    const client = createClient(config, true);
    let connected = false;
    let stopping = false;

    const stop = () => {
      if (stopping) return;
      stopping = true;
      logInfo('Stopping listener — unsubscribing and disconnecting...');
      stopListenClient(client, config.topics || [], () => {
        emitEvent({ type: 'closed' });
        resolve();
      });
    };

    client.on('connect', () => {
      connected = true;
      emitEvent({ type: 'connected', clientId: config.clientId });
      logInfo('Listening for MQTT messages...');

      config.topics.forEach((topic) => {
        client.subscribe(topic, { qos: 1 }, (err) => {
          if (err) {
            emitEvent({ type: 'error', message: `Subscribe failed for ${topic}: ${err.message}` });
            return;
          }
          emitEvent({ type: 'subscribed', topic });
          logInfo(`Subscribed: ${topic}`);
        });
      });
    });

    client.on('message', (topic, payload, packet) => {
      emitEvent({
        type: 'message',
        topic,
        payloadBase64: payload.toString('base64'),
        retain: !!packet.retain,
        receivedAt: new Date().toISOString(),
      });
    });

    client.on('error', (err) => {
      emitEvent({ type: 'error', message: err.message });
      logInfo(`MQTT client error: ${err.message}`);
    });

    client.on('close', () => {
      emitEvent({ type: 'closed' });
      logInfo('MQTT connection closed');
    });

    client.on('offline', () => {
      emitEvent({ type: 'offline' });
      logInfo('MQTT client offline');
    });

    client.on('reconnect', () => {
      emitEvent({ type: 'reconnecting' });
      logInfo('MQTT client reconnecting...');
    });

    const timer = setTimeout(() => {
      if (!connected) {
        client.end(true);
        reject(new Error(`Timed out after ${config.timeoutMs}ms waiting for broker connection`));
      }
    }, config.timeoutMs + 1000);

    client.on('connect', () => clearTimeout(timer));

    watchStdinForStop(stop);
    process.on('SIGINT', stop);
    process.on('SIGTERM', stop);
  });
}

async function main() {
  logInfo('--- MQTT bridge (OpenSSL) ---');

  const cli = parseArgs(process.argv);
  const stdin = await readFirstStdinJson();
  const config = buildConfig(cli, stdin);

  logInfo(`Action: ${config.action}`);

  if (config.action === 'validate') {
    await runValidate(config);
    logInfo('Validation succeeded');
    process.exit(0);
  }

  if (config.action === 'publish') {
    await runPublish(config);
    logInfo('Publish succeeded');
    process.exit(0);
  }

  if (config.action === 'listen') {
    await runListen(config);
    process.exit(0);
  }

  fail(`Unknown action: ${config.action}`);
}

main().catch((err) => {
  fail(err && err.message ? err.message : String(err));
});
