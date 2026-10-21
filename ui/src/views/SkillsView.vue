<script setup lang="ts">
import { t } from '../i18n'
import { computed, onMounted, reactive, ref } from 'vue'
import { ApiError, type NewSkill, type PartnerApiSurface, type RuntimeSettings, type SkillView } from '../api'
import Dialog from '../components/Dialog.vue'
import Drawer from '../components/Drawer.vue'
import HintTip from '../components/HintTip.vue'
import Icon from '../components/Icon.vue'
import MarkdownBody from '../components/MarkdownBody.vue'
import SaveBar from '../components/SaveBar.vue'
import { showToast } from '../toast'

/**
 * The skills the workspace holds, and which of them are in use.
 *
 * <para>
 * A skill can be added here (written by hand, or uploaded as a SKILL.md or a zip) and removed, but not edited: its
 * text belongs to whoever wrote it, so changing it means uploading a new one. Whether this installation wants it
 * injected into every prompt is a separate decision made here, and it lives in settings so replacing a skill file
 * does not silently switch it back on.
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

/** The hidden file input behind 「上传技能」; the visible button only opens it. */
const fileInput = ref<HTMLInputElement | null>(null)
const busy = ref(false)
/** The skill whose delete is armed: the first press arms, the second deletes. */
const armed = ref<string | null>(null)

const composing = ref(false)
const draft = reactive<NewSkill>({ skillId: '', name: '', description: '', instructions: '' })
const formError = ref('')

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

/** The server's reason without the status prefix the client puts in front of it. */
function reasonOf(failure: unknown): string {
  return failure instanceof ApiError ? failure.message.replace(/^\d+:\s*/, '') : String(failure)
}

/**
 * Re-reads the skills and their warnings only. Not `refresh`: that also resets the switches to what is stored, and
 * adding a skill must not throw away a toggle the person has made but not saved yet.
 */
async function reloadList() {
  const loaded = await props.api.skills()
  skills.value = loaded.skills
  warnings.value = loaded.warnings
}

/** The server drops a deleted skill from the deny list; this keeps the page's copy of it in step. */
function forget(skillId: string) {
  disabled.value = disabled.value.filter((id) => id !== skillId)
  if (stored.value !== null) {
    stored.value = key((JSON.parse(stored.value) as string[]).filter((id) => id !== skillId))
  }
  if (baseline.value) {
    baseline.value = { ...baseline.value, skills: { disabled: baseline.value.skills.disabled.filter((id) => id !== skillId) } }
  }
}

function pickFile() {
  fileInput.value?.click()
}

async function onFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  // Cleared so choosing the same file again (after fixing it) still fires `change`.
  input.value = ''
  if (!file) return

  busy.value = true
  error.value = ''
  notice.value = ''
  try {
    const added = await props.api.uploadSkill(file, file.name)
    await reloadList()
    showToast(t('已添加技能 {name}', { name: added.name }))
    emit('changed')
  } catch (failure) {
    error.value = reasonOf(failure)
  } finally {
    busy.value = false
  }
}

function openComposer() {
  Object.assign(draft, { skillId: '', name: '', description: '', instructions: '' })
  formError.value = ''
  composing.value = true
}

async function create() {
  if (draft.skillId.trim() === '' || draft.name.trim() === '' || draft.description.trim() === '' || draft.instructions.trim() === '') {
    formError.value = t('四项都要填')
    return
  }

  busy.value = true
  formError.value = ''
  try {
    const added = await props.api.createSkill({ ...draft, skillId: draft.skillId.trim() })
    composing.value = false
    await reloadList()
    showToast(t('已添加技能 {name}', { name: added.name }))
    emit('changed')
  } catch (failure) {
    formError.value = reasonOf(failure)
  } finally {
    busy.value = false
  }
}

async function remove(skill: SkillView) {
  if (armed.value !== skill.skillId) {
    armed.value = skill.skillId
    return
  }

  armed.value = null
  error.value = ''
  try {
    await props.api.deleteSkill(skill.skillId)
    forget(skill.skillId)
    await reloadList()
    showToast(t('已删除'))
    emit('changed')
  } catch (failure) {
    error.value = reasonOf(failure)
  }
}

async function save() {
  if (!baseline.value) {
    error.value = t('先读取设置')
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
    notice.value = t('已保存，助手已重新加载')
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
              {{ t('{enabledCount} / {length} 启用', { enabledCount: enabledCount, length: skills.length }) }}
              <template v-if="warnings.length > 0"> {{ t('· {length} 条警告', { length: warnings.length }) }}</template>
            </span>
            <button class="ghost refresh" :aria-label="t('刷新')" :title="t('刷新')" data-test="skills-refresh" @click="refresh">
              <Icon name="refresh" :size="14" />
            </button>
          </p>
          <label v-if="skills.length >= SEARCH_FROM" class="search-field">
            <Icon name="search" :size="14" class="search-glyph" />
            <input v-model="filter" :placeholder="t('搜索技能')" :aria-label="t('搜索技能')" data-test="skills-filter" />
          </label>
          <div class="actions">
            <input ref="fileInput" type="file" accept=".md,.zip" hidden data-test="skills-file" @change="onFile" />
            <button class="ghost" :disabled="busy" data-test="skills-upload" @click="pickFile">
              <Icon name="upload" :size="14" /> {{ t('上传技能') }}
            </button>
            <HintTip>{{ t('选一个 SKILL.md，或把技能文件夹压缩成 .zip（SKILL.md 放在根目录）再上传。') }}</HintTip>
            <button class="primary" :disabled="busy" data-test="skills-new" @click="openComposer">
              <Icon name="plus" :size="14" :stroke-width="2.2" /> {{ t('创建技能') }}
            </button>
          </div>
        </div>

        <!-- Shown rather than swallowed: a skill that did not load is invisible otherwise, and "it just is not
             there" is the worst way to find out you wrote the front matter wrong. One line until it is asked for. -->
        <details v-if="warnings.length > 0" class="warnings" data-test="skills-warnings">
          <summary>
            {{ t('有 {length} 个技能没能加载', { length: warnings.length }) }}
            <HintTip>{{ t('通常是技能文件开头的格式写错了。展开可以看到具体是哪一个。') }}</HintTip>
          </summary>
          <ul>
            <li v-for="(warning, index) in warnings" :key="index" class="break">{{ warning }}</li>
          </ul>
        </details>

        <p v-if="skills.length === 0" class="empty" data-test="skills-empty">
          {{ t('还没有技能。上传或创建一个，或把技能放在') }} <code>&lt;workspace&gt;/skills/&lt;id&gt;/SKILL.md</code>{{ t('。') }}
        </p>

        <div class="grid">
          <section v-for="skill in visible" :key="skill.skillId" class="skill g-glass" :class="{ 'is-off': !isOn(skill.skillId) }" data-test="skills-item">
            <div class="skill-head">
              <span class="disc" :class="`hue-${hue(skill)}`" aria-hidden="true">{{ monogram(skill) }}</span>
              <span class="name">{{ skill.name }}</span>
              <HintTip align="end"><span class="id">{{ t('ID：') }}<code>{{ skill.skillId }}</code>{{ t('。关闭后助手不会再使用这个技能。') }}</span></HintTip>
            </div>

            <p class="desc break">{{ skill.description }}</p>

            <div class="skill-foot">
              <div class="foot-actions">
                <button class="ghost read" data-test="skills-instructions-toggle" @click="reading = skill">
                  <Icon name="file" :size="14" /> {{ t('查看说明') }}
                </button>
                <button
                  class="ghost read danger"
                  :class="{ 'is-armed': armed === skill.skillId }"
                  data-test="skills-delete"
                  @click="remove(skill)"
                  @blur="armed = null"
                >
                  <Icon name="trash" :size="14" /> {{ armed === skill.skillId ? t('确认删除') : t('删除') }}
                </button>
              </div>
              <input
                type="checkbox"
                class="g-switch"
                role="switch"
                :aria-label="t('启用 {name}', { name: skill.name })"
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

    <Dialog :open="composing" :title="t('创建技能')" @close="composing = false">
      <div class="composer-body" data-test="skills-composer">
        <label class="field">
          <span class="field-name">{{ t('技能 ID') }} <HintTip>{{ t('文件夹名，只能用小写字母、数字、点、下划线和连字符。助手用它来加载这个技能。') }}</HintTip></span>
          <input v-model="draft.skillId" placeholder="code-review" spellcheck="false" data-test="skills-new-id" />
        </label>
        <label class="field">
          <span class="field-name">{{ t('名称') }}</span>
          <input v-model="draft.name" :placeholder="t('例如：代码评审')" data-test="skills-new-name" />
        </label>
        <label class="field">
          <span class="field-name">{{ t('描述') }} <HintTip>{{ t('一句话说明什么时候该用它。助手靠这句话决定要不要加载。') }}</HintTip></span>
          <input v-model="draft.description" :placeholder="t('例如：审查一次代码改动，指出风险')" data-test="skills-new-description" />
        </label>
        <label class="field">
          <span class="field-name">{{ t('说明') }}</span>
          <textarea v-model="draft.instructions" rows="8" :placeholder="t('助手加载这个技能后要照着做的步骤。')" data-test="skills-new-instructions" />
        </label>
        <p v-if="formError" class="error" role="alert" data-test="skills-new-error">{{ formError }}</p>
      </div>
      <template #footer>
        <button class="ghost" data-test="skills-new-cancel" @click="composing = false">{{ t('取消') }}</button>
        <button class="primary" :disabled="busy" data-test="skills-new-submit" @click="create">{{ t('创建') }}</button>
      </template>
    </Dialog>

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

.actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}

.composer-body {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--space-4);
}

.field {
  display: grid;
  gap: var(--space-2);
}

.field-name {
  display: inline-flex;
  align-items: center;
  gap: var(--space-2);
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

.foot-actions {
  display: flex;
  align-items: center;
  gap: var(--space-1);
}

/* A quiet delete: red only once armed or hovered, so a grid of skills is not a grid of red buttons. */
.read.danger {
  color: var(--text-3);
}

.read.danger:hover:not(:disabled),
.read.danger.is-armed {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}
</style>
