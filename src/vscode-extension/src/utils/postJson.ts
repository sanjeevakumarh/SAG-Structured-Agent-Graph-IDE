import * as http from 'http';

/** Minimal POST helper that sends JSON and returns parsed JSON. Throws on non-2xx or timeout. */
export function postJson<T>(baseUrl: string, path: string, body: unknown): Promise<T> {
    return new Promise((resolve, reject) => {
        const url = new URL(`${baseUrl.replace(/\/$/, '')}${path}`);
        const payload = JSON.stringify(body);
        const opts: http.RequestOptions = {
            hostname: url.hostname,
            port: url.port,
            path: url.pathname,
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(payload),
            },
            timeout: 15_000,
        };
        const req = http.request(opts, (res) => {
            let data = '';
            res.on('data', (chunk: string) => data += chunk);
            res.on('end', () => {
                if ((res.statusCode ?? 0) >= 400) {
                    reject(new Error(`HTTP ${res.statusCode}: ${data.substring(0, 200)}`));
                } else {
                    try { resolve(JSON.parse(data) as T); }
                    catch { reject(new Error('Invalid JSON')); }
                }
            });
        });
        req.on('error', reject);
        req.on('timeout', () => { req.destroy(); reject(new Error('Request timed out')); });
        req.write(payload);
        req.end();
    });
}
