self.assetsManifest = self.assetsManifest || {};

const cacheName = `offline-cache-${self.assetsManifest.version}`;
const include = [/\\.(dll|pdb|wasm|html|js|json|css|png|svg|ico|woff2?|ttf)$/];

self.addEventListener('install', event => event.waitUntil(caches.open(cacheName).then(cache =>
    cache.addAll(self.assetsManifest.assets
        .filter(asset => include.some(pattern => pattern.test(asset.url)) && asset.url !== 'service-worker.js')
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }))))));

self.addEventListener('activate', event => event.waitUntil(caches.keys().then(keys =>
    Promise.all(keys.filter(key => key.startsWith('offline-cache-') && key !== cacheName).map(key => caches.delete(key))))));

self.addEventListener('fetch', event => event.respondWith(caches.match(event.request).then(response => response || fetch(event.request))));
