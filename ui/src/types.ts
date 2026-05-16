export type EventStatus =
  | 'Received'
  | 'Forwarding'
  | 'Forwarded'
  | 'ForwardFailed'
  | 'Replaying'
  | 'ReplayFailed'
  | 'Captured'

export interface EventSummary {
  id: string
  provider: string
  path: string
  receivedAt: string
  status: EventStatus
  signatureValid: boolean | null
  forwardStatusCode: number | null
  replayCount: number
}

export interface EventDetail extends EventSummary {
  headers: Record<string, string>
  body: string
  forwardUrl: string
  forwardedAt: string | null
  forwardError: string | null
  lastReplayAt: string | null
  lastError: string | null
  validationDetails: ValidationDetails | null
}

export interface ValidationDetails {
  isValid: boolean
  algorithmUsed: string
  computedSignature: string
  receivedSignature: string
  payloadUsed: string
  extractedTimestamp: string | null
  error: string | null
}

export interface ListEventsResponse {
  items: EventSummary[]
  total: number | null
  limit: number
  offset: number
  totalApproximate: boolean
}

export interface HealthResponse {
  status: string
  version: string
  providers: string[]
  database: string
  eventCount: number
  oldestEvent: string | null
}

export interface EventNotification {
  id: string
  provider: string
  status: string
}
