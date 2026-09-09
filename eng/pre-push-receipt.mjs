// Git supplies pre-push refs as: local_ref local_sha remote_ref remote_sha. The receipt protects
// the landing ref; feature-branch pushes are checked by the landing gate instead. A deletion has
// no tree to attest to, even when the deleted remote ref is main.
export function receiptCheckForPushRefs(input) {
  const refs = String(input).split(/\r?\n/).filter(Boolean).map(line => {
    const [localRef, localSha, remoteRef, remoteSha] = line.trim().split(/\s+/)
    return {localRef, localSha, remoteRef, remoteSha}
  })
  const contentRefs = refs.filter(ref => ref.localSha && /[^0]/.test(ref.localSha))
  if (contentRefs.length === 0) return {verify: false, skipped: false}
  const pushesMain = contentRefs.some(ref => ref.remoteRef === 'refs/heads/main')
  return {verify: pushesMain, skipped: !pushesMain}
}
