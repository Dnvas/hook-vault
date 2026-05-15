import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'

export function useEvents(filters: { provider?: string; status?: string }) {
  return useQuery({
    queryKey: ['events', filters],
    queryFn: () => api.listEvents({ ...filters, limit: 100, offset: 0 }),
    refetchInterval: 10_000,
  })
}

export function useInvalidateEvents() {
  const qc = useQueryClient()
  return () => qc.invalidateQueries({ queryKey: ['events'] })
}
