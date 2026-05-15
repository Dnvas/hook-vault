import { useEffect } from 'react'
import { getToken } from '../api/client'

export function useEventStream(onNotification: () => void) {
  useEffect(() => {
    const token = getToken()
    if (!token) return

    const url = `/api/events/stream?token=${encodeURIComponent(token)}`
    const es = new EventSource(url)

    es.onmessage = () => {
      onNotification()
    }

    es.onerror = () => {
      // EventSource auto-reconnects; no action needed here
    }

    return () => es.close()
  }, [onNotification])
}
