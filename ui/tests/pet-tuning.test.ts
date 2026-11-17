import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { PartnerApiSurface, PetModelInfo, PetModelProfile } from '../src/api'
import PetTuningPanel from '../src/views/PetTuningPanel.vue'

const model: PetModelInfo = {
  id: 'cat', name: 'Cat', model3: 'cat.model3.json', motions: [], expressions: [], hasHitAreas: false, bytes: 1,
  parameters: [{ id: 'PARAM_ANGLE_X', name: 'Angle', groupId: null }], warnings: ['metadata warning'],
}
const profile: PetModelProfile = { motions: {}, expressions: {}, headBottom: 0.35, faceBottom: 0.55 }

function setup() {
  const petDiagnostics = vi.fn(async () => ({
    modelId: 'cat',
    actions: [{ action: 'stretch', kind: 'procedural' as const, parameters: [{ channel: 'ParamAngleX', id: 'PARAM_ANGLE_X', min: -30, max: 30 }], missing: ['ParamAngleY'] }],
  }))
  const api = { petDiagnostics } as unknown as PartnerApiSurface
  const wrapper = mount(PetTuningPanel, { props: { model, profile, api, actions: ['stretch'] } })
  return { wrapper, petDiagnostics }
}

describe('pet tuning editor', () => {
  it('emits explicit channel mapping and model gain without mutating its input', async () => {
    const { wrapper } = setup()
    await wrapper.get('[data-test="pet-channel-ParamAngleX"]').setValue('PARAM_ANGLE_X')
    expect(wrapper.get('#pet-candidates-ParamAngleX option').attributes('value')).toBe('PARAM_ANGLE_X')
    const mapped = wrapper.emitted('change')?.at(-1)?.[0] as PetModelProfile
    expect(mapped.parameters).toEqual({ ParamAngleX: 'PARAM_ANGLE_X' })
    expect(profile.parameters).toBeUndefined()
    await wrapper.setProps({ profile: mapped })
    await wrapper.get('[data-test="pet-amplitude"]').setValue('-1.5')
    expect((wrapper.emitted('change')?.at(-1)?.[0] as PetModelProfile).amplitude).toBe(-1.5)
    expect(wrapper.text()).toContain('metadata warning')
  })

  it('creates a continuous extra track at the real action duration and edits its keys', async () => {
    const { wrapper } = setup()
    const action = wrapper.get('[data-test="pet-tuning-stretch"]')
    await action.findAll('button').at(-1)!.trigger('click')
    let next = wrapper.emitted('change')?.at(-1)?.[0] as PetModelProfile
    expect(next.procedural?.stretch?.extraTracks?.[0]?.keys).toEqual([{ at: 0, value: 0 }, { at: 1.3, value: 1 }, { at: 2.6, value: 0 }])
    await wrapper.setProps({ profile: next })
    await action.get('input[list="pet-parameter-candidates"]').setValue('PARAM_ANGLE_X')
    next = wrapper.emitted('change')?.at(-1)?.[0] as PetModelProfile
    expect(next.procedural?.stretch?.extraTracks?.[0]?.id).toBe('PARAM_ANGLE_X')
    await wrapper.setProps({ profile: next })
    const values = action.findAll('input[type="number"]')
    await values[4]!.setValue('-2')
    next = wrapper.emitted('change')?.at(-1)?.[0] as PetModelProfile
    expect(next.procedural?.stretch?.extraTracks?.[0]?.keys[1]?.value).toBe(-2)
  })

  it('reports only the loaded model diagnostics and clears them on model change', async () => {
    const { wrapper, petDiagnostics } = setup()
    await wrapper.get('[data-test="pet-diagnostics-refresh"]').trigger('click')
    await flushPromises()
    expect(petDiagnostics).toHaveBeenCalledOnce()
    expect(wrapper.get('[data-test="pet-diagnostics"]').text()).toContain('PARAM_ANGLE_X [-30, 30]')
    expect(wrapper.get('[data-test="pet-diagnostics"]').text()).toContain('ParamAngleY')
    await wrapper.setProps({ model: { ...model, id: 'other' } })
    expect(wrapper.find('[data-test="pet-diagnostics"]').exists()).toBe(false)
  })
})
