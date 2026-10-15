# Codex Instructions

Shared skills are **not** stored in this repository. They live in a sibling checkout of
`panoramicdata/PanoramicData.Skills`, reached at:

    ../PanoramicData.Skills/.github/skills

That relative path is correct from this repository's root, and commands in a skill's `SKILL.md` are
written to be run from the repository root with the prefix already included.

Before starting work here:

- Read `.github/copilot-instructions.md` for repository-wide conventions when relevant.
- Read `../PanoramicData.Skills/.github/skills/copilot-instructions.md` before selecting a skill.
- When a request matches a folder under `../PanoramicData.Skills/.github/skills`, read that skill's
  `SKILL.md` completely and follow its relative references.

## If the skills checkout is missing

**If `../PanoramicData.Skills/.github/skills` does not exist, stop.** Tell the user the shared skills
checkout is missing and must be cloned as a sibling of this repository:

    git clone https://github.com/panoramicdata/PanoramicData.Skills.git

Do not improvise a substitute for a skill, do not copy skills into this repository, and do not report
the skill as unavailable for some other reason. The path is missing, and saying so plainly is what
lets the user fix it in one step.

## Working directory matters

The path resolves only when the working directory is a **direct child** of the shared repos folder
(the repository root itself, or a git worktree created as a true sibling). A working copy one level
deeper cannot see it. If you need a worktree, create it with the `git-worktree` skill, which places
it correctly:

    ../PanoramicData.Skills/.github/skills/git-worktree/GitWorktree.ps1 -Action new -Name <ticket>

## Never edit skills from here

Everything under `../PanoramicData.Skills` is shared with every other repository, so a local edit
silently changes behaviour everywhere. Fixes belong upstream: raise an issue in
`panoramicdata/PanoramicData.Skills`, make the change there, and commit it there.
