<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { PartnerApiSurface, RuntimeSettings, SkillView } from '../api'

/**
 * The skills the workspace holds, and which of them are in use.
 *
 * <para>
 * Read-only about content, editable about use — the same split the storage chose. A <c>SKILL.md</c> belongs to
 * whoever wrote it; whether this installation wants it injected into every prompt is a decision made here, and
 * it lives in settings so replacing a skill file does not silently switch it back on.
 * </para>
 */
const props = defineProps<{ api: PartnerApiSurface }>()
const emit = defineEmits<{ context: [value: string]; changed: [] }>()

const skills = ref<SkillView[]>([])
const warnings = ref<string[]>([])
const disabled = ref<string[]>([])
const baseline = ref<RuntimeSettings | null>(null)
const error = ref('')
const notice = ref('')
const open = ref<string | null>(null)

const enabledCount = computed(() => skills.value.filter((skill) => !disabled.value.includes(skill.skillId)).length)
function isOn(skillId: string): boolean {
  return !disabled.value.includes(skillId)
}

function toggle(skillId: string) {
  disabled.value = isOn(skillId)
    ? [...disabled.value, skillId]
    : disabled.value.filter((id) => id !== skillId)
}

async function refresh() {
  error.value = ''
  try {
    const [loaded, settings] = await Promise.all([props.api.skills(), props.api.settings()])
    skills.value = loaded.skills
    warnings.value = loaded.warnings
    baseline.value = settings
    disabled.value = settings.skills.disabled

    emit('context', `技能 · ${skills.value.length - disabled.value.length}/${skills.value.length} 启用`)
  } catch (failure) {
    error.value = String(failure)
  }
}

async function save() {
  if (!baseline.value) {
    error.value = '先读取设置'
    return
  }

  error.value = ''
  notice.value = ''
  try {
    // The norm (ui.md): re-read after writing, so what is on screen is what the server kept.
    const saved = await props.api.saveSettings({ ...baseline.value, skills: { disabled: disabled.value } })
    baseline.value = saved.settings
    disabled.value = saved.settings.skills.disabled
    notice.value = '已保存 · 消费者已重启'
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)
defineExpose({ refresh })
</script>

<template>
  <div class="view">
    <div class="toolbar">
      <button data-test="skills-refresh" @click="refresh">刷新</button>
      <span class="spacer" />
      <span class="muted" data-test="skills-summary">
        {{ enabledCount }} / {{ skills.length }} 启用
        <template v-if="warnings.length > 0"> · {{ warnings.length }} 条警告</template>
      </span>
    </div>

    <div class="scroll">
      <p v-if="skills.length === 0" class="muted" data-test="skills-empty">
        （工作区里没有技能。技能放在 <code>&lt;workspace&gt;/skills/&lt;id&gt;/SKILL.md</code>。）
      </p>

      <section v-for="skill in skills" :key="skill.skillId" class="skill" :data-test="'skills-item'">
        <label class="head">
          <input
            type="checkbox"
            :checked="isOn(skill.skillId)"
            :data-test="'skills-toggle'"
            @change="toggle(skill.skillId)"
          />
          <span class="name">{{ skill.name }}</span>
          <code class="id">{{ skill.skillId }}</code>
        </label>

        <p class="desc break">{{ skill.description }}</p>

        <button
          class="ghost"
          :data-test="'skills-instructions-toggle'"
          @click="open = open === skill.skillId ? null : skill.skillId"
        >
          {{ open === skill.skillId ? '收起说明' : '看说明' }}
        </button>
        <pre v-if="open === skill.skillId" class="instructions">{{ skill.instructions }}</pre>
      </section>

      <div v-if="warnings.length > 0" class="card">
        <h2>扫描警告</h2>
        <!-- Shown rather than swallowed: a skill that did not load is invisible otherwise, and "it just is not
             there" is the worst way to find out you wrote the front matter wrong. -->
        <ul class="warnings">
          <li v-for="(warning, index) in warnings" :key="index">{{ warning }}</li>
        </ul>
      </div>

      <div class="actions">
        <button class="primary" data-test="skills-save" @click="save">保存</button>
        <span v-if="notice" class="muted" data-test="skills-notice">{{ notice }}</span>
      </div>

      <p v-if="error" class="error notice" data-test="error">{{ error }}</p>
    </div>
  </div>
</template>

<style scoped>
.scroll {
  flex: 1;
  min-block-size: 0;
  overflow-y: auto;
  padding: var(--space-3);
  display: grid;
  gap: var(--space-2);
  align-content: start;
}

.skill {
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  padding: var(--space-2);
  display: grid;
  gap: var(--space-1);
  justify-items: start;
}

.head {
  display: flex;
  gap: var(--space-2);
  align-items: baseline;
  inline-size: 100%;
}

.name {
  font-size: var(--fs-title);
}

.id {
  color: var(--text-3);
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
  font-size: var(--fs-meta);
}

.desc {
  margin: 0;
  color: var(--text-2);
}

.instructions {
  inline-size: 100%;
  margin: 0;
  padding: var(--space-2);
  background: var(--surface-0);
  border-radius: var(--radius-sm);
  max-block-size: 18rem;
  overflow: auto;
  font-family: var(--font-mono);
  font-size: var(--fs-code);
  line-height: var(--lh-code);
  font-size: var(--fs-meta);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

/* Layout only; the surface comes from styles.css. This used to re-declare border, radius and padding without
   the background, which is the whole reason the warnings card here did not match the cards on the settings
   page despite using the same token names. */

.card h2 {
  /* 14px, two steps under the global 20px heading — a warning card's title, not a page title. */
  font-size: var(--fs-ui);
  margin: 0 0 var(--space-1);
}

.warnings {
  margin: 0;
  padding-inline-start: var(--space-4);
  color: var(--warn);
  font-size: var(--fs-meta);
}

.actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
</style>
