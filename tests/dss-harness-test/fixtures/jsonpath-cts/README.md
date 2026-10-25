# The JSONPath compliance test suite

`cts.json` is the JSONPath Compliance Test Suite of RFC 9535's working group, taken whole and unchanged from
`https://github.com/jsonpath-standard/jsonpath-compliance-test-suite` at commit
`20ebdcce5008c8a777ee9c80ad853ecf606950b5` (2026-10-13), under its BSD-2 license, beside it in `LICENSE`.

`JsonPathComplianceTests` asks the tool's own JSONPath (`src/DssHarness.Core/Json`) every case of it: a selector it
must refuse is refused, and every other one selects what the case says - in its order, or in one of the orders a case
with `results` allows, where an object's members may come in any order.

To take a newer suite, replace `cts.json` with the file at the newer commit and name that commit here.
