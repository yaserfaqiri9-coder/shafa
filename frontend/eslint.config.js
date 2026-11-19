import pluginVue from 'eslint-plugin-vue'
import { withVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'

// Report potential errors without changing existing source files automatically.
export default withVueTs(
  { rootDir: import.meta.dirname },
  { ignores: ['dist/**', 'node_modules/**', 'coverage/**', 'test-results/**'] },
  pluginVue.configs['flat/essential'],
  vueTsConfigs.recommended,
)
