// Relative URL on purpose: works unchanged in every environment.
// - `ng serve` (local dev): proxy.conf.json forwards /api -> localhost:8080
// - Docker/Kubernetes: nginx.conf (in the built image) proxies /api -> the
//   backend Service internally, so the browser only ever talks to this app's
//   own origin.
export const API_BASE_URL = '/api';
