import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'

export function useEvent(id: string | null) {
  return useQuery({
    queryKey: ['event', id],
    queryFn: () => api.getEvent(id!),
    enabled: id != null,
  })
}

export function useInvalidateEvent() {
  const qc = useQueryClient()
  return (id: string) => qc.invalidateQueries({ queryKey: ['event', id] })
}
