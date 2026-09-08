// Line-level baseline splice.
// argv: file prevTotal prevPassed prevNotExecuted total passed notExecuted narrativeFile
// narrativeFile is JSON: { recordedAt, cause, detectedBy }. Never a JSON round-trip of the baseline:
// the hand-written prose in the file survives because only the totals block, recordedAt and the
// deltaFromPrevious block are rewritten.
const fs = require("fs");
const [p, prevTotal, prevPassed, prevNotExecuted, total, passed, notExecuted, nf] = process.argv.slice(2);
if (!nf) throw new Error("usage: splice-generic.js file prevTotal prevPassed prevNotExecuted total passed notExecuted narrativeFile");
if (Number(total) - Number(notExecuted) !== Number(passed)) throw new Error("passed must equal total - notExecuted");
const n = JSON.parse(fs.readFileSync(nf, "utf8"));
const added = Number(total) - Number(prevTotal);
const lines = fs.readFileSync(p, "utf8").split("\n");
const delta = {
  previousTotals: { total: Number(prevTotal), passed: Number(prevPassed), failed: 0, notExecuted: Number(prevNotExecuted) },
  change: "total " + prevTotal + " -> " + total + "; passed " + prevPassed + " -> " + passed + "; failed unchanged at 0; notExecuted " + prevNotExecuted + " -> " + notExecuted,
  cause: n.cause,
  detectedBy: n.detectedBy,
  arithmeticCrossCheck: prevTotal + " + " + added + " = " + total + "; passed = total - notExecuted = " + total + " - " + notExecuted + " = " + passed + ".",
};
lines[5] = '  "recordedAt": ' + JSON.stringify(n.recordedAt) + ",";
const deltaText = JSON.stringify({ deltaFromPrevious: delta }, null, 2).split("\n").slice(1, -1).join("\n");
const start = lines.findIndex((l) => l.startsWith('  "deltaFromPrevious"'));
const end = lines.findIndex((l, i) => i > start && l === "  },");
lines.splice(start, end - start + 1, deltaText + ",");
let t = lines.join("\n");
// The totals block has no trailing comma after notExecuted (it is the last key of the object).
const block = (tot, pas, ne) => '"total": ' + tot + ',\n    "passed": ' + pas + ',\n    "failed": 0,\n    "notExecuted": ' + ne + '\n';
if (!t.includes(block(prevTotal, prevPassed, prevNotExecuted))) throw new Error("totals block did not match previous totals");
t = t.replace(block(prevTotal, prevPassed, prevNotExecuted), block(total, passed, notExecuted));
fs.writeFileSync(p, t);
const j = JSON.parse(fs.readFileSync(p, "utf8"));
console.log("spliced and JSON-valid", j.totals.total, j.totals.passed, j.totals.notExecuted);
