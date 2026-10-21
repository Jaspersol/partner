import { createApp } from 'vue'
import App from './App.vue'
import { initLocale } from './i18n'
import './tokens.css'
import './styles.css'

initLocale()
createApp(App).mount('#app')
