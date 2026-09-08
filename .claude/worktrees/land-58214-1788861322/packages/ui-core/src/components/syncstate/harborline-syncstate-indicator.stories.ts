import type { Meta, StoryObj } from '@storybook/web-components';
import { html } from 'lit';
import './harborline-syncstate-indicator.js';

const meta: Meta = {
  title: 'Core/SyncStateIndicator',
  component: 'harborline-syncstate-indicator',
  parameters: {
    a11y: {
      harborline: {
        wcag22Conformant: ['1.3.1', '1.4.3', '1.4.11', '4.1.2', '4.1.3'],
        ariaPattern: 'https://www.w3.org/WAI/ARIA/apg/practices/live-regions/',
        keyboardMap: [],
        focus: { initial: 'none', trap: false, restore: null },
        screenReaderAudit: {
          'nvda-2026.1/firefox-126': { verified: '2026-04-28', auditor: '@a11y-lead', pass: true },
          'voiceover-macos15/safari-17': { verified: '2026-04-28', auditor: '@a11y-lead', pass: true },
        },
        contrast: { bodyTextMinRatio: 4.5, borderMinRatio: 3.0, apcaLcNonTextMin: 45 },
        targetSize: { desktop: 24, tablet: 32, mobile: 44 },
        shadowDom: { mode: 'open', crossRootAriaStrategy: 'reflective-aria' },
        composedOf: [],
        directionalIcons: ['conflict'],
      },
    },
  },
};

export default meta;
type Story = StoryObj;

export const AllStates: Story = {
  render: () => html`
    <div style="display: flex; flex-direction: column; gap: 12px; padding: 16px;">
      <harborline-syncstate-indicator state="healthy"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="stale"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="offline"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="conflict"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="quarantine"></harborline-syncstate-indicator>
    </div>
  `,
};

export const Compact: Story = {
  render: () => html`
    <div style="display: flex; gap: 16px; padding: 16px;">
      <harborline-syncstate-indicator state="healthy" form="compact"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="stale" form="compact"></harborline-syncstate-indicator>
      <harborline-syncstate-indicator state="conflict" form="compact"></harborline-syncstate-indicator>
    </div>
  `,
};
