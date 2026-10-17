// Reporting only: these counts do not authorize debt, flakes, retries or a gate result.
// Debt is the reviewed ledger's budget, never a scan of the runtime source surface.
const debtLabel = 'reviewed debt-ledger budget'
const flakeLabel = 'flake registrations'
const ceilingKey = '# reviewed-aggregate-debt-ceiling'
const unavailable = (label, reason) => ({label, status: 'unavailable', reason})
const isCount = value => Number.isSafeInteger(value) && value >= 0
const countIn = value => typeof value === 'string' && /^\d+$/.test(value) && isCount(Number(value)) ? Number(value) : null
const available = (label, used, ceiling) => ({label, status: 'available', used, ceiling, headroom: ceiling - used})

export function reviewedDebtBudgetHeadroom(tsv) {
  if (typeof tsv !== 'string') return unavailable(debtLabel, 'reviewed debt ledger unavailable')
  const lines = tsv.split(/\r?\n/).map(line => line.trim()).filter(Boolean)
  const headers = lines.filter(line => line.split('\t')[0] === ceilingKey)
  if (headers.length !== 1) return unavailable(debtLabel, 'expected one reviewed ceiling header')
  const fields = headers[0].split('\t')
  const ceiling = fields.length === 2 ? countIn(fields[1]) : null
  if (ceiling === null) return unavailable(debtLabel, 'invalid reviewed ceiling')
  let used = 0
  const paths = new Set()
  for (const line of lines.filter(value => !value.startsWith('#'))) {
    const [file, tenant, actor] = line.split('\t')
    const tenantBudget = countIn(tenant), actorBudget = countIn(actor)
    if (!file || paths.has(file) || tenantBudget === null || actorBudget === null) {
      return unavailable(debtLabel, 'invalid or duplicate reviewed budget row')
    }
    paths.add(file)
    used += tenantBudget + actorBudget
    if (!isCount(used)) return unavailable(debtLabel, 'reviewed budget total is not a safe count')
  }
  return available(debtLabel, used, ceiling)
}

export function flakeRegistryHeadroom(rows, ceiling) {
  if (!Array.isArray(rows)) return unavailable(flakeLabel, 'flake registry unavailable')
  if (!isCount(ceiling)) return unavailable(flakeLabel, 'invalid flake ceiling')
  return available(flakeLabel, rows.length, ceiling)
}

export function formatBaselineHeadroom(summary) {
  return summary.status === 'available'
    ? `${summary.label}: ${summary.used} of ${summary.ceiling}; headroom ${summary.headroom}`
    : `${summary.label}: unavailable (${summary.reason})`
}
