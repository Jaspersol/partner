// @vitest-environment node
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const dist = new URL('../node_modules/easy-live2d/dist/', import.meta.url)

describe('installed easy-live2d physics hook', () => {
  for (const file of ['index.js', 'index.cjs']) {
    it(`${file} applies the hook after effects and before pose and physics`, () => {
      const source = readFileSync(new URL(file, dist), 'utf8')
      expect(source).toMatch(/this\._lipsync\s*\);\s*if \(this\._beforePhysics\)\s*this\._beforePhysics\(this\._frameParameters, deltaTime\);\s*if \(this\._pose\)[\s\S]*?if \(this\._physics\)/)
      expect(source).toContain('this._model?.setBeforePhysics(callback)')
      expect(source).toContain('this._model.setBeforePhysics(this._beforePhysics)')
    })
  }

  for (const file of ['index.d.ts', 'index.d.cts']) {
    it(`${file} exposes the nullable callback and frame parameters`, () => {
      const source = readFileSync(new URL(file, dist), 'utf8')
      expect(source).toContain('onBeforePhysics(callback: ((parameters: Live2DFrameParameters, deltaTime: number) => void) | null): void')
      expect(source).toMatch(/export \{[^}]*type Live2DFrameParameters/)
    })
  }
})
