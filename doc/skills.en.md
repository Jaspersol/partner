# Skills

[简体中文](./skills.md) | [繁體中文](./skills.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

A skill is a `SKILL.md` containing a name, a one-line description and a procedure. Once it is placed in the workspace, the skill appears in the model’s system prompt.

## File format

Location: `<workspace>/skills/<skill-id>/SKILL.md`

```markdown
---
name: Code review
description: Review a code change and point out risks
---
Write the steps the assistant should follow here.
```

- The first line must be `---`; everything through the next `---` is the header. `name` and `description` are required, and other keys are ignored.
- The skill ID is the directory name. It may contain lowercase letters, digits, dots, underscores and hyphens, and must begin with a letter or digit.
- The description is one line and is truncated after 200 characters. At most 50 skills are loaded.
- Invalid skills are skipped with a warning on the page; they do not stop the application.

## Managing skills

Under **Settings → Skills** you can:

- **Upload** a `SKILL.md`, or upload a `.zip` of the skill folder with `SKILL.md` at its root.
- **Create** a skill by entering its ID, name, description and instructions.
- **Enable / disable** it. Disabled skills are not loaded. The switch is stored in settings, so replacing a file does not silently enable it.
- **Delete** it.
- There is no editor. The skill text belongs to its author; upload a new version to change it.

Adding or removing a skill takes effect on the next turn and does not require a consumer restart.

## Injected content

At the beginning of each turn, the system prompt receives a list in this form:

```
Available skills (load one with the skill tool before using it):
- <skill-id> — <name>: <description>
```

No list is added when there are no skills.

## Known limitation

The instruction tells the model to load a skill with the skill tool before using it, but the current code contains no registration for that tool. For now the model can see only the ID, name and description, not the procedure itself. This still needs to be implemented.
