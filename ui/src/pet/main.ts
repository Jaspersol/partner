// First, before anything imports a Pixi renderer: Pixi 8 compiles its uniform code with `new Function` unless this
// shim is in place, and the pet page's CSP forbids `unsafe-eval` on purpose. Import order is the whole point here.
import 'pixi.js/unsafe-eval'

import { bootPetApp } from './PetApp'

void bootPetApp()