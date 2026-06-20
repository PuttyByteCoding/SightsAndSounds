// Component-test setup (#129): registers the jest-dom matchers
// (toBeInTheDocument, toHaveTextContent, …) for the `components` vitest
// project. Auto-cleanup between tests is handled by the svelteTesting()
// vite plugin configured in vitest.config.ts.
import '@testing-library/jest-dom/vitest';
