# Skills

[简体中文](./skills.md) | [繁體中文](./skills.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

A skill is one `SKILL.md`: a name, a one-line description and the steps. Once it is in the workspace, the skill list appears in the model's system prompt.

## File format

Location: `<workspace>/skills/<skill-id>/SKILL.md`

```markdown
---
name: Code review
description: Review a code change and point out risks
---
The steps the assistant should follow go here.
```

- The first line must be `---`; the header runs until the next `---`. `name` and `description` are required; other keys are ignored.
- The skill ID is the folder name: lowercase letters, digits, dots, underscores and hyphens, starting with a letter or digit.
- The description is one line, truncated past 200 characters. At most 50 skills are loaded.
- A malformed skill is skipped with a warning on the page; nothing crashes.

## Managing them

The **Skills** page in the sidebar:

- **Upload**: pick a `SKILL.md`, or zip a skill folder (with `SKILL.md` at the root).
- **Create**: fill in skill ID, name, description and instructions.
- **Enable / disable**: disabled skills are not loaded. The switch lives in settings, so replacing a skill file does not quietly turn it back on.
- **Delete**.
- No editing. A skill's text belongs to its author; to change it, upload a new one.

Adding or removing a skill applies on the next turn without restarting the consumer.

## What gets injected

Before each turn, the system prompt gets a list:

```
Available skills (load one with the skill tool before using it):
- <skill-id> — <name>: <description>
```

Nothing is injected when there are no skills.

## Known limits

The list tells the model to load a skill with the skill tool before using it, but no code registering such a tool was found. So for now the model sees the ID, name and description, not the steps. To be added.
