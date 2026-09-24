# Hash chain (PR-15)

Tamper evidence for the ledgers without slowing commands down (v2.1 §8, ADR-037, Patch 1 P-2, E-PR15-1…8).

## What happens when

| Moment | What | Who |
| --- | --- | --- |
| Command transaction | Each ledger row stores its own `row_hash`; a trigger registers the row's group in `audit.integrity_state` as PENDING_SEAL | Application role |
| Every 5 s | `LedgerSealer`: per chain, recomputes each pending group from its rows, appends a seal (`chain_hash = SHA256(prev ‖ sequence ‖ group_hash)`), marks it SEALED — or SEAL_ERROR if a row no longer matches its stored hash | `rochell_sealer` |
| 00:15 local | `LedgerDigester`: Merkle root of the previous day's chain hashes, linked to the previous digest, signed (ECDSA P-256) and written once to WORM, recorded in `audit.ledger_digest` | `rochell_sealer` |
| On demand / nightly | `VerifyHashChain` (`hash:verify`): recompute everything from the data, compare with the seals and with the digests **read from WORM** | Auditor, Controller |

Chains and groups: GL = one journal (header + entries); INV_QTY / INV_VALUE = the rows of one source event;
DOMAIN_EVENT = the events of one command. A sealed group cannot receive rows (the trigger rejects it).

## What verification reports

The first invalid `ledger_sequence` per chain (an altered row breaks its group and every later chain hash), sequence gaps
(a deleted seal), digests whose Merkle root differs from WORM or whose signature fails, groups PENDING_SEAL for more than
10 minutes (sealer down), and SEAL_ERROR groups. Repair never edits data: restore from backup or document with the auditor.

## Operations

- Keys: the signing key lives only with the sealing service (secret store); verifiers need the public key.
- WORM: `FileSystemWormStore` only in TEST environments. Production needs S3 Object Lock in compliance mode at a second
  provider (**B-03**). The e-mail copy of each digest is deferred until mail infrastructure exists.
- Hosting of the sealer loop (`LedgerSealer.RunAsync`) and the 00:15 digest job arrives with the API host (PR-18).
