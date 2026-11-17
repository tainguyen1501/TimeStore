import type { MetricsSampleResponse } from "@/services/api/metrics-service"

/** Per-second rate of a per-interval count. */
export const perSecond = (value: number, intervalSeconds: number) =>
  intervalSeconds > 0 ? value / intervalSeconds : 0

/** "12.3/s", "1.2k/s". */
export function formatRate(value: number) {
  if (value >= 1000) return `${(value / 1000).toFixed(1)}k/s`
  return `${value < 10 ? value.toFixed(1) : Math.round(value)}/s`
}

/** "4 ms", "250 ms", "2.5 s", "—" without requests. */
export function formatLatency(ms: number | null | undefined) {
  if (ms === null || ms === undefined) return "—"
  return ms >= 1000 ? `${ms / 1000} s` : `${ms} ms`
}

/** The last sample (the most recent 10 s), or undefined while none exists. */
export const lastSample = (samples: MetricsSampleResponse[]) => samples[samples.length - 1]

/** A series of per-second rates from the samples. */
export const rates = (
  samples: MetricsSampleResponse[],
  pick: (sample: MetricsSampleResponse) => number,
  intervalSeconds: number
) => samples.map((sample) => perSecond(pick(sample), intervalSeconds))
