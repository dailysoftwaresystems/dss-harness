# Measured registries of the section-header defect

Four arm registries from a consumer's run in which the by-arm merge wrote a section header twice
(`refresh-agent --merge`) and refused an arm both sides had changed alike (`fold-agent`):

| File | What it was |
|---|---|
| `base.arms.registry` | the registry both sides last held |
| `main.arms.registry` | the main tree's |
| `agent-refreshed.arms.registry` | the agent's, at the refresh that wrote the header twice |
| `agent-folded.arms.registry` | another agent's, at the fold that refused the arm |

They are made neutral, never edited by hand: every distinct comment line became `# comment NNNN`
(`# section NNNN -- its arms` where it was a section's header), every arm id `arm-NNNNN` (its spellings
that differed in case still differing in case alone), and every row's text after its arm ` tail NNNNNN`,
one table shared by the four files. Blank lines, row kinds and line endings are as they were. So every
question the merge asks of a line - blank, comment or row, the arm a row names ignoring case, its text -
has the same answer here as in the original, and the merge of these files is the merge of those.
