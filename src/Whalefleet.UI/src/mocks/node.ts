import { setupServer } from 'msw/node'
import { handlers } from './index'

/**
 * Node 端（vitest）mock server。
 *
 * 与浏览器共用同一份 handler —— 这是选 MSW 而非「自写 Vite 中间件」的主要收益：
 * 测试里断言的响应结构与开发时实际拿到的**逐字相同**，不会两套 mock 各写各的。
 *
 * 用法见 `tests/setup.ts` 与 `tests/mock-contract.spec.ts`。
 */
export const mockServer = setupServer(...handlers)