import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { defineConfig, type Plugin } from 'vite';
import pkg from './package.json' with { type: 'json' };

const root = import.meta.dirname;
const publicDir = join(root, 'public');

/** Files in public/ that are served but not needed offline. */
const NOT_PRECACHED = new Set(['robots.txt']);

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

/**
 * Turns the build into an installable, offline-first PWA without a plugin framework:
 *  - sw.js gets the exact list of files to keep offline, and a version that changes whenever
 *    any of them does (that's what makes browsers pick up an update);
 *  - _headers (Cloudflare Pages) gets the hash of the one inline script for its CSP.
 */
function tackPwa(): Plugin {
  return {
    name: 'tack-pwa',
    apply: 'build',
    enforce: 'post',
    generateBundle(_, bundle) {
      const hash = createHash('sha256');
      const urls = new Set<string>(['/index.html']);
      for (const [fileName, item] of Object.entries(bundle).sort(([a], [b]) => a.localeCompare(b))) {
        if (fileName.endsWith('.map')) continue;
        urls.add(`/${fileName}`);
        hash.update(fileName);
        hash.update(item.type === 'chunk' ? item.code : item.source);
      }
      for (const path of walk(publicDir).sort()) {
        const rel = relative(publicDir, path).replaceAll('\\', '/');
        if (rel.startsWith('_') || NOT_PRECACHED.has(rel)) continue;
        urls.add(`/${rel}`);
        hash.update(rel);
        hash.update(readFileSync(path));
      }
      const version = hash.digest('hex').slice(0, 12);
      const sw = readFileSync(join(root, 'src/sw.js'), 'utf8')
        .replace('__TACK_VERSION__', version)
        .replace('__TACK_PRECACHE__', JSON.stringify([...urls].sort(), null, 2));
      this.emitFile({ type: 'asset', fileName: 'sw.js', source: sw });

      const html = bundle['index.html'];
      if (!html || html.type !== 'asset') this.error('index.html missing from bundle');
      const inline = [...String(html.source).matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
      if (inline.length !== 1) this.error(`Expected exactly one inline script, found ${inline.length}`);
      const scriptHash = createHash('sha256').update(inline[0]).digest('base64');
      // LF endings whatever the checkout uses (the repo is CRLF; Cloudflare expects plain lines).
      const headers = readFileSync(join(root, 'deploy/_headers'), 'utf8')
        .split(/\r?\n/)
        .join('\n')
        .replace('__BOOT_SCRIPT_HASH__', `sha256-${scriptHash}`);
      this.emitFile({ type: 'asset', fileName: '_headers', source: headers });
    },
  };
}

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  build: {
    target: ['es2022', 'safari16', 'chrome111', 'firefox113'],
    sourcemap: false,
    assetsInlineLimit: 0,
    cssCodeSplit: false,
    modulePreload: { polyfill: false },
  },
  server: { port: 5173 },
  preview: { port: 4173 },
  plugins: [tackPwa()],
});
