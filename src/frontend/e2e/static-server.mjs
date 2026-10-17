/**
 * Minimal static file server for Playwright e2e (Next.js `out/` export).
 */
import fs from 'fs';
import http from 'http';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, '..', 'out');
const port = Number(process.env.PORT || 4173);

const mimeTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

function sendFile(res, filePath) {
  const ext = path.extname(filePath);
  const type = mimeTypes[ext] || 'application/octet-stream';
  fs.readFile(filePath, (err, data) => {
    if (err) {
      res.writeHead(404);
      res.end('Not found');
      return;
    }
    res.writeHead(200, { 'Content-Type': type });
    res.end(data);
  });
}

http
  .createServer((req, res) => {
    const urlPath = decodeURIComponent(new URL(req.url || '/', 'http://localhost').pathname);
    let filePath = path.join(root, urlPath);

    if (urlPath.endsWith('/')) {
      filePath = path.join(filePath, 'index.html');
    } else if (!path.extname(filePath) && fs.existsSync(`${filePath}.html`)) {
      filePath = `${filePath}.html`;
    } else if (!path.extname(filePath) && fs.existsSync(path.join(filePath, 'index.html'))) {
      filePath = path.join(filePath, 'index.html');
    }

    if (!filePath.startsWith(root)) {
      res.writeHead(403);
      res.end('Forbidden');
      return;
    }

    fs.stat(filePath, (err, stat) => {
      if (err || !stat.isFile()) {
        sendFile(res, path.join(root, '404.html'));
        return;
      }
      sendFile(res, filePath);
    });
  })
  .listen(port, '0.0.0.0', () => {
    process.stdout.write(`static-server listening on ${port}\n`);
  });
