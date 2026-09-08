#!/usr/bin/env node
// Tiny stdlib-only helper the two-process e2e harness uses instead of `jq` and `lsof`
// (ticket 258 — neither is installed on the development machine, node always is).
// Every command that needs a string value takes it from an ENV VAR, never argv, so
// message bodies and party ids never pass through shell quoting.
//
//   readStdin JSON commands:
//     get <dotted.path>   -> print the value at the path ("" when absent/null)
//     authorof            -> env MATCH_BODY: authorPartyId of the first message whose body matches
//     countof             -> env MATCH_BODY: how many messages have that body
//     authors             -> comma-joined unique authorPartyId across all messages
//   no-stdin commands:
//     joinbody <fixture>  -> render the join-body fixture, substituting ${NAME} from env
//     msgbody             -> env MSG_BODY: {"body": "..."}
//     hex <bytes>         -> random hex (openssl rand -hex replacement)
//     seed                -> env SEED_NAME: deterministic sha256 hex of the name
//     freeport            -> an OS-assigned free TCP port on 127.0.0.1
//     listening <port>    -> exit 0 if something accepts a connection on that port, else 1
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import net from 'node:net';

const [cmd, arg] = process.argv.slice(2);
const readStdin = () => readFileSync(0, 'utf8');
const parse = () => { try { return JSON.parse(readStdin()); } catch { return null; } };
const messages = (doc) => (Array.isArray(doc?.messages) ? doc.messages : []);

switch (cmd) {
  case 'get': {
    const value = arg.split('.').reduce((node, key) => (node == null ? node : node[key]), parse());
    process.stdout.write(value == null ? '' : String(value));
    break;
  }
  case 'authorof': {
    const hit = messages(parse()).find((m) => m?.body === process.env.MATCH_BODY);
    process.stdout.write(hit?.authorPartyId == null ? '' : String(hit.authorPartyId));
    break;
  }
  case 'countof':
    process.stdout.write(String(messages(parse()).filter((m) => m?.body === process.env.MATCH_BODY).length));
    break;
  case 'authors':
    process.stdout.write([...new Set(messages(parse()).map((m) => m?.authorPartyId).filter(Boolean))].join(','));
    break;
  case 'joinbody': {
    // The FIXTURE is the contract: its key set is asserted against the route's request model by
    // JoinBodyFixtureContractTests, so a route field added or renamed fails a test instead of the harness.
    const rendered = readFileSync(arg, 'utf8').replace(/\$\{(\w+)\}/g, (_, name) => {
      const value = process.env[name];
      if (!value) throw new Error(`join-body fixture placeholder \${${name}} has no value in the environment`);
      return JSON.stringify(value).slice(1, -1);
    });
    JSON.parse(rendered); // fail loudly on a malformed fixture rather than POSTing garbage
    process.stdout.write(rendered);
    break;
  }
  case 'msgbody':
    process.stdout.write(JSON.stringify({ body: process.env.MSG_BODY }));
    break;
  case 'hex':
    process.stdout.write(randomBytes(Number(arg)).toString('hex'));
    break;
  case 'seed':
    process.stdout.write(createHash('sha256').update(`${process.env.SEED_NAME}-comms-e2e-root-seed-v1`).digest('hex'));
    break;
  case 'freeport': {
    const server = net.createServer();
    server.listen(0, '127.0.0.1', () => {
      process.stdout.write(String(server.address().port));
      server.close();
    });
    break;
  }
  case 'listening': {
    const socket = net.connect(Number(arg), '127.0.0.1');
    socket.setTimeout(1000);
    socket.on('connect', () => { socket.destroy(); process.exit(0); });
    socket.on('timeout', () => { socket.destroy(); process.exit(1); });
    socket.on('error', () => process.exit(1));
    break;
  }
  default:
    process.stderr.write(`unknown command: ${cmd}\n`);
    process.exit(2);
}
