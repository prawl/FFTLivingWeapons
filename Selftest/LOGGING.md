# Logging

The stand-in mod's verb table. A mod keeps the same table in its docs/LOGGING.md, and the log
contract holds it and the LogVerb enum in lockstep.

| Verb | Level discipline |
|---|---|
| `startup` | Info for the launch header. |
| `gate` | Info when a gate refuses something a player can see. |
| `trace` | Debug only, the ids and hex that explain the line above it. |
