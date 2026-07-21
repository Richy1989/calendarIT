import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { ensureAccessToken } from '../auth/session'

// Attaches a valid JWT to every request, refreshing it first if it's about to expire.
const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = await ensureAccessToken()
    if (token) {
      request.headers.set('Authorization', `Bearer ${token}`)
    }
    return request
  },
}

/**
 * Fully-typed API client. Request bodies and responses are inferred from the generated
 * OpenAPI schema, so the client stays in lock-step with the backend. Same-origin base
 * URL ("/") works with the Vite dev proxy and with the SPA served by the backend in prod.
 */
export const api = createClient<paths>({ baseUrl: '/' })
api.use(authMiddleware)
