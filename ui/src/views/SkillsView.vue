<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import type { PartnerApiSurface, RuntimeSettings, SkillView } from '../api'
import Drawer from '../components/Drawer.vue'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import MarkdownBody from '../components/MarkdownBody.vue'
import SaveBar from '../components/SaveBar.vue'

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
const emit = defineEmits<{ changed: [] }>()

const skills = ref<SkillView[]>([])
const warnings = ref<string[]>([])
const disabled = ref<string[]>([])
const baseline = ref<RuntimeSettings | null>(null)
const error = ref('')
const notice = ref('')
/** The skill whose full instructions are open in the drawer. */
const reading = ref<SkillView | null>(null)
const filter = ref('')

/** The deny list as last stored; "changed" is a comparison, not a flag. */
const stored = ref<string | null>(null)
const key = (list: string[]) => JSON.stringify([...list].sort())
const dirty = computed(() => stored.value !== null && key(disabled.value) !== stored.value)

const enabledCount = computed(() => skills.value.filter((skill) => !disabled.value.includes(skill.skillId)).length)

/** A search box only earns its place once the list is long enough to need one. */
const SEARCH_FROM = 8
const visible = computed(() => {
  const text = filter.value.trim().toLowerCase()
  return text === ''
    ? skills.value
    : skills.value.filter((skill) => `${skill.name} ${skill.description}`.toLowerCase().includes(text))
})

function isOn(skillId: string): boolean {
  return !disabled.value.includes(skillId)
}

function toggle(skillId: string) {
  disabled.value = isOn(skillId)
    ? [...disabled.value, skillId]
    : disabled.value.filter((id) => id !== skillId)
}

/** The first letter of the name, drawn on a coloured disc: the page's only picture, and it costs no file. */
function monogram(skill: SkillView): string {
  return (skill.name.trim()[0] ?? skill.skillId[0] ?? '?').toUpperCase()
}

/** A stable colour per skill from six quiet ones, so the same skill is the same colour on every visit. */
function hue(skill: SkillView): number {
  let sum = 0
  for (const character of skill.skillId) {
    sum = (sum * 31 + character.charCodeAt(0)) >>> 0
  }
  return sum % 6
}

async function refresh() {
  error.value = ''
  try {
    const [loaded, settings] = await Promise.all([props.api.skills(), props.api.settings()])
    skills.value = loaded.skills
    warnings.value = loaded.warnings
    baseline.value = settings
    disabled.value = [...settings.skills.disabled]
    stored.value = key(disabled.value)
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
    // Re-read after writing, so what is on screen is what the server kept.
    const saved = await props.api.saveSettings({ ...baseline.value, skills: { disabled: disabled.value } })
    baseline.value = saved.settings
    disabled.value = [...saved.settings.skills.disabled]
    stored.value = key(disabled.value)
    notice.value = '已保存，助手已重新加载'
    emit('changed')
  } catch (failure) {
    error.value = String(failure)
  }
}

onMounted(refresh)

/** Asked by the shell before it switches away: the views are `v-if`, so leaving discards every edit. */
function hasUnsavedChanges(): boolean {
  return dirty.value
}

defineExpose({ refresh, hasUnsavedChanges })
</script>

<template>
  <div class="view">
    <div class="scroll skills-scroll">
      <div class="g-page is-wide">
        <div class="head">
          <p class="summary-line">
            <span data-test="skills-summary">
              {{ enabledCount }} / {{ skills.length }} 启用
              <template v-if="warnings.length > 0"> · {{ warnings.length }} 条警告</template>
            </span>
            <button class="ghost refresh" aria-label="刷新" title="刷新" data-test="skills-refresh" @click="refresh">
              <Icon name="refresh" :size="14" />
            </button>
          </p>
          <label v-if="skills.length >= SEARCH_FROM" class="search-field">
            <Icon name="search" :size="14" class="search-glyph" />
            <input v-model="filter" placeholder="搜索技能" aria-label="搜索技能" data-test="skills-filter" />
          </label>
        </div>

        <!-- Shown rather than swallowed: a skill that did not load is invisible otherwise, and "it just is not
             there" is the worst way to find out you wrote the front matter wrong. One line until it is asked for. -->
        <details v-if="warnings.length > 0" class="warnings" data-test="skills-warnings">
          <summary>
            有 {{ warnings.length }} 个技能没能加载
            <HintTip>通常是技能文件开头的格式写错了。展开可以看到具体是哪一个。</HintTip>
          </summary>
          <ul>
            <li v-for="(warning, index) in warnings" :key="index" class="break">{{ warning }}</li>
          </ul>
        </details>

        <p v-if="skills.length === 0" class="empty" data-test="skills-empty">
          还没有技能。把技能放在 <code>&lt;workspace&gt;/skills/&lt;id&gt;/SKILL.md</code>。
        </p>

        <div class="grid">
          <section v-for="skill in visible" :key="skill.skillId" class="skill g-glass" :class="{ 'is-off': !isOn(skill.skillId) }" data-test="skills-item">
            <div class="skill-head">
              <span class="disc" :class="`hue-${hue(skill)}`" aria-hidden="true">{{ monogram(skill) }}</span>
              <span class="name">{{ skill.name }}</span>
              <HintTip align="end"><span class="id">ID：<code>{{ skill.skillId }}</code>。关闭后助手不会再使用这个技能。</span></HintTip>
            </div>

            <p class="desc break">{{ skill.description }}</p>

            <div class="skill-foot">
              <button class="ghost read" data-test="skills-instructions-toggle" @click="reading = skill">
                <Icon name="file" :size="14" /> 查看说明
              </button>
              <input
                type="checkbox"
                class="g-switch"
                role="switch"
                :aria-label="`启用 ${skill.name}`"
                :checked="isOn(skill.skillId)"
                data-test="skills-toggle"
                @change="toggle(skill.skillId)"
              />
            </div>
          </section>
        </div>
      </div>
    </div>

    <Drawer :open="reading !== null" :title="reading?.name ?? ''" wide @close="reading = null">
      <MarkdownBody v-if="reading" :body="reading.instructions" />
    </Drawer>

    <SaveBar
      prefix="skills"
      status-test="skills-notice"
      :dirty="dirty"
      :status="notice"
      :error="error"
      can-discard
      @save="save"
      @discard="refresh"
    />
  </div>
</template>

<style scoped>
.skills-scroll {
  flex: 1;
  min-block-size: 0;
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
}

.summary-line {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin: 0;
  color: var(--text-3);
  font-size: var(--fs-meta);
}

.refresh {
  min-block-size: 1.75rem;
  inline-size: 1.75rem;
  padding: 0;
  border-radius: var(--radius-sm);
}

.warnings {
  padding: var(--space-3) var(--space-4);
  border-radius: var(--radius-sm);
  background: color-mix(in srgb, var(--warn) 12%, transparent);
  color: var(--warn);
  font-size: var(--fs-ui);
}

.warnings summary {
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.warnings ul {
  margin: var(--space-2) 0 0;
  padding-inline-start: var(--space-5);
  font-size: var(--fs-meta);
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(18rem, 1fr));
  gap: var(--space-4);
}

.skill {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  grid-template-rows: auto 1fr auto;
  gap: var(--space-3);
  padding: var(--space-4) var(--space-5);
  transition: opacity var(--motion-base) var(--ease);
}

.skill.is-off {
  opacity: 0.62;
}

.skill-head {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-inline-size: 0;
}

.disc {
  flex: none;
  display: inline-grid;
  place-items: center;
  inline-size: 2.25rem;
  block-size: 2.25rem;
  border-radius: 50%;
  font-weight: 600;
  background: color-mix(in srgb, var(--disc, var(--accent)) 16%, transparent);
  color: var(--disc, var(--accent));
}

.hue-0 {
  --disc: var(--tool-read);
}

.hue-1 {
  --disc: var(--tool-write);
}

.hue-2 {
  --disc: var(--tool-search);
}

.hue-3 {
  --disc: var(--tool-web);
}

.hue-4 {
  --disc: var(--tool-record);
}

.hue-5 {
  --disc: var(--tool-agent);
}

.name {
  flex: 1;
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 600;
}

.desc {
  margin: 0;
  color: var(--text-2);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.skill-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.read {
  min-block-size: 2rem;
  padding-inline: var(--space-2);
  color: var(--text-2);
  font-size: var(--fs-meta);
}
</style>
