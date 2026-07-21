import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { getAccessToken } from '../auth/authStorage'

// Attaches the stored JWT access token to every outgoing request.
const authMiddleware: Middleware = {
  onRequest({ request }) {
    const token = getAccessToken()
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
