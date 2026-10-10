import { screen, waitFor, within } from '@testing-library/react'
import { http } from 'msw'
import { describe, expect, it } from 'vitest'

import { problem } from '../../../mocks/handlers/problem'
import type { StoredChannel } from '../../../mocks/handlers/projects'
import { server } from '../../../mocks/server'
import { openOwnerSettings, requestsTo } from '../../../test/project-settings'

const SLACK_URL = 'https://hooks.slack.com/services/T000/B000/abcdefSECRET'
const NEW_SLACK_URL = 'https://hooks.slack.com/services/T000/B000/ghijklNEWSECRET'
const HMAC = 'hmac-signing-key-123'

const webhookChannel: StoredChannel = {
  id: '00000000-0000-7000-8000-00000000c001',
  type: 'webhook',
  target: 'https://example.com/hooks/aide',
  secret: 'old-hmac-key',
  events: ['gate_required'],
}

const slackChannel: StoredChannel = {
  id: '00000000-0000-7000-8000-00000000c002',
  type: 'slack',
  target: '',
  secret: SLACK_URL,
  events: ['gate_required', 'run_failed'],
}

async function openNotifications(channels: StoredChannel[]) {
  const utils = await openOwnerSettings('notifications', { channels: channels.map((c) => ({ ...c })) })
  await screen.findByRole('heading', { name: 'Notifications' })
  return utils
}

function channelGroup(n: number) {
  return screen.getByRole('group', { name: `Channel ${String(n)}` })
}

async function save(user: Awaited<ReturnType<typeof openNotifications>>['user']) {
  await user.click(screen.getByRole('button', { name: 'Save notifications' }))
}

function lastSentChannels() {
  const sent = requestsTo('PATCH', '/settings/notifications')
  const body = sent[sent.length - 1]?.body as { channels: Record<string, unknown>[] } | undefined
  return body?.channels ?? []
}

describe('Settings → Notifications: webhook signing key (FR-9)', () => {
  it('FR-9: a saved signing key shows "Set" and is kept when saving untouched', async () => {
    const { user, project } = await openNotifications([webhookChannel])
    const group = channelGroup(1)

    expect(within(group).getByText('Channel 1 signing key:').parentElement).toHaveTextContent('Set')
    expect(within(group).queryByLabelText(/signing key/i, { selector: 'input' })).not.toBeInTheDocument()
    await save(user)

    expect(await screen.findByText('Notification settings saved.')).toBeInTheDocument()
    const [sent] = lastSentChannels()
    expect(sent).toMatchObject({ id: webhookChannel.id, type: 'webhook', target: webhookChannel.target })
    expect(sent).not.toHaveProperty('secret')
    expect(sent).not.toHaveProperty('clearSecret')
    expect(project.channels[0]?.secret).toBe('old-hmac-key')
  })

  it('FR-9: Replace sends a new signing key in a password field', async () => {
    const { user, project } = await openNotifications([webhookChannel])

    await user.click(screen.getByRole('button', { name: 'Replace channel 1 signing key' }))
    const input = screen.getByLabelText('New channel 1 signing key')
    expect(input).toHaveFocus()
    expect(input).toHaveAttribute('type', 'password')
    await user.type(input, HMAC)
    await save(user)

    await screen.findByText('Notification settings saved.')
    expect(lastSentChannels()[0]).toMatchObject({ secret: HMAC })
    expect(project.channels[0]?.secret).toBe(HMAC)
    expect(document.body).not.toHaveTextContent(HMAC)
  })

  it('FR-47: a saved signing key can be replaced but not cleared (webhook bodies are always signed)', async () => {
    await openNotifications([webhookChannel])
    const group = channelGroup(1)

    expect(within(group).getByRole('button', { name: 'Replace channel 1 signing key' })).toBeInTheDocument()
    expect(within(group).queryByRole('button', { name: /^Clear/ })).not.toBeInTheDocument()
  })

  it('FR-47: replacing with a blank signing key is rejected before saving', async () => {
    const { user, project } = await openNotifications([webhookChannel])

    await user.click(screen.getByRole('button', { name: 'Replace channel 1 signing key' }))
    const input = screen.getByLabelText('New channel 1 signing key')
    await user.type(input, '   ')
    await save(user)

    expect(
      await screen.findByRole('link', { name: 'Channel 1: Enter a signing key: webhook bodies are always HMAC-signed.' }),
    ).toHaveAttribute('href', `#${input.id}`)
    expect(requestsTo('PATCH', '/settings/notifications')).toHaveLength(0)
    expect(project.channels[0]?.secret).toBe('old-hmac-key')
  })

  it('FR-47: a new webhook channel requires a signing key, reported in the error summary and linked to the field', async () => {
    const { user, project } = await openNotifications([])

    await user.click(screen.getByRole('button', { name: 'Add channel' }))
    await user.selectOptions(screen.getByLabelText('Channel 1 type'), 'webhook')
    await user.type(screen.getByLabelText('Channel 1 webhook URL'), 'https://example.com/hook')
    const input = screen.getByLabelText('Channel 1 signing key')
    expect(input).toHaveAttribute('aria-required', 'true')
    expect(input).toHaveAttribute('type', 'password')
    expect(screen.queryByLabelText(/signing key \(optional\)/)).not.toBeInTheDocument()

    await save(user)
    const link = await screen.findByRole('link', {
      name: 'Channel 1: Enter a signing key: webhook bodies are always HMAC-signed.',
    })
    expect(link).toHaveAttribute('href', `#${input.id}`)
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(requestsTo('PATCH', '/settings/notifications')).toHaveLength(0)

    await user.type(input, HMAC)
    await save(user)

    await screen.findByText('Notification settings saved.')
    expect(lastSentChannels()[0]).toMatchObject({ id: null, type: 'webhook', target: 'https://example.com/hook', secret: HMAC })
    expect(project.channels[0]?.secret).toBe(HMAC)
    await waitFor(() => {
      expect(within(channelGroup(1)).getByText('Channel 1 signing key:').parentElement).toHaveTextContent('Set')
    })
  })

  it('FR-47: a saved webhook channel without a key (hasSecret false) must be given one', async () => {
    const { user } = await openNotifications([{ ...webhookChannel, secret: null }])

    expect(within(channelGroup(1)).getByText('Channel 1 signing key:').parentElement).toHaveTextContent('Not set')
    const input = screen.getByLabelText('Channel 1 signing key')
    expect(input).toHaveAttribute('aria-required', 'true')
    await save(user)

    expect(
      await screen.findByRole('link', { name: 'Channel 1: Enter a signing key: webhook bodies are always HMAC-signed.' }),
    ).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/notifications')).toHaveLength(0)

    await user.type(input, HMAC)
    await save(user)
    await screen.findByText('Notification settings saved.')
    expect(lastSentChannels()[0]).toMatchObject({ id: webhookChannel.id, secret: HMAC })
  })

  it('FR-47: server VALIDATION for a missing signing key is shown on the field', async () => {
    server.use(
      http.patch('*/api/projects/:id/settings/notifications', () =>
        problem(400, 'VALIDATION', 'The notification settings are invalid.', {
          errors: { 'channels[0].secret': ['Enter a signing secret: webhook bodies are always HMAC-signed.'] },
        }),
      ),
    )
    const { user } = await openNotifications([webhookChannel])

    await user.click(screen.getByRole('button', { name: 'Replace channel 1 signing key' }))
    const input = screen.getByLabelText('New channel 1 signing key')
    await user.type(input, HMAC)
    await save(user)

    const link = await screen.findByRole('link', {
      name: 'Channel 1: Enter a signing secret: webhook bodies are always HMAC-signed.',
    })
    expect(link).toHaveAttribute('href', `#${input.id}`)
    expect(input).toHaveAttribute('aria-invalid', 'true')
  })
})

describe('Settings → Notifications: Slack/Microsoft Teams webhook URL is a secret (FR-9)', () => {
  it('FR-9: a saved Slack URL shows only the masked host, offers Replace but not Clear, and is kept on save', async () => {
    const { user, project } = await openNotifications([slackChannel])
    const group = channelGroup(1)

    const status = within(group).getByText('Channel 1 Slack webhook URL:').parentElement
    expect(status).toHaveTextContent('Set (hooks.slack.com/…)')
    expect(document.body).not.toHaveTextContent('abcdefSECRET')
    expect(within(group).getByRole('button', { name: 'Replace channel 1 Slack webhook URL' })).toBeInTheDocument()
    expect(within(group).queryByRole('button', { name: /^Clear/ })).not.toBeInTheDocument()
    // No plain target field for Slack.
    expect(within(group).queryByLabelText('Channel 1 webhook URL')).not.toBeInTheDocument()

    await save(user)

    await screen.findByText('Notification settings saved.')
    const [sent] = lastSentChannels()
    expect(sent).toMatchObject({ id: slackChannel.id, type: 'slack' })
    expect(sent).not.toHaveProperty('secret')
    expect(sent).not.toHaveProperty('target')
    expect(sent).not.toHaveProperty('clearSecret')
    expect(project.channels[0]?.secret).toBe(SLACK_URL)
  })

  it('FR-9: Replace sends the new Slack URL in `secret`', async () => {
    const { user, project } = await openNotifications([slackChannel])

    await user.click(screen.getByRole('button', { name: 'Replace channel 1 Slack webhook URL' }))
    const input = screen.getByLabelText('New channel 1 Slack webhook URL')
    expect(input).toHaveFocus()
    await user.type(input, NEW_SLACK_URL)
    await save(user)

    await screen.findByText('Notification settings saved.')
    expect(lastSentChannels()[0]).toMatchObject({ id: slackChannel.id, type: 'slack', secret: NEW_SLACK_URL })
    expect(project.channels[0]?.secret).toBe(NEW_SLACK_URL)
    expect(document.body).not.toHaveTextContent('ghijklNEWSECRET')
  })

  it('FR-9: a new Slack channel requires an https URL, reported in the error summary and linked to the field', async () => {
    const { user, project } = await openNotifications([])

    await user.click(screen.getByRole('button', { name: 'Add channel' }))
    await user.selectOptions(screen.getByLabelText('Channel 1 type'), 'slack')
    const input = screen.getByLabelText('Channel 1 Slack webhook URL')
    expect(input).toHaveAttribute('aria-required', 'true')
    expect(screen.queryByLabelText('Channel 1 email address')).not.toBeInTheDocument()

    await save(user)
    const link = await screen.findByRole('link', { name: 'Channel 1: Enter the Slack incoming-webhook URL.' })
    expect(link).toHaveAttribute('href', `#${input.id}`)
    expect(input).toHaveAttribute('aria-invalid', 'true')

    await user.type(input, 'http://hooks.slack.com/insecure')
    await save(user)
    expect(await screen.findByRole('link', { name: 'Channel 1: Use the https:// incoming-webhook URL from Slack.' })).toBeInTheDocument()
    expect(requestsTo('PATCH', '/settings/notifications')).toHaveLength(0)

    await user.clear(input)
    await user.type(input, SLACK_URL)
    await save(user)

    await screen.findByText('Notification settings saved.')
    const [sent] = lastSentChannels()
    expect(sent).toMatchObject({ id: null, type: 'slack', secret: SLACK_URL })
    expect(sent).not.toHaveProperty('target')
    expect(project.channels[0]?.secret).toBe(SLACK_URL)
    await waitFor(() => {
      expect(within(channelGroup(1)).getByText('Channel 1 Slack webhook URL:').parentElement).toHaveTextContent(
        'Set (hooks.slack.com/…)',
      )
    })
    expect(document.body).not.toHaveTextContent('abcdefSECRET')
  })

  it('FR-9: changing a saved Slack channel to Teams needs a new URL (the saved one is not kept across types)', async () => {
    const { user } = await openNotifications([slackChannel])

    await user.selectOptions(screen.getByLabelText('Channel 1 type'), 'teams')
    const input = screen.getByLabelText('Channel 1 Microsoft Teams webhook URL')
    expect(within(channelGroup(1)).getByText('Channel 1 Microsoft Teams webhook URL:').parentElement).toHaveTextContent('Not set')

    await save(user)
    expect(await screen.findByRole('link', { name: 'Channel 1: Enter the Microsoft Teams incoming-webhook URL.' })).toHaveAttribute(
      'href',
      `#${input.id}`,
    )
    expect(requestsTo('PATCH', '/settings/notifications')).toHaveLength(0)
  })

  it('FR-9: a new Teams channel sends its URL as the secret, next to a webhook channel with a plain target', async () => {
    const { user } = await openNotifications([])

    await user.click(screen.getByRole('button', { name: 'Add channel' }))
    await user.selectOptions(screen.getByLabelText('Channel 1 type'), 'webhook')
    await user.type(screen.getByLabelText('Channel 1 webhook URL'), 'https://example.com/hook')
    await user.type(screen.getByLabelText('Channel 1 signing key'), HMAC)
    await user.click(screen.getByRole('button', { name: 'Add channel' }))
    await user.selectOptions(screen.getByLabelText('Channel 2 type'), 'teams')
    await user.type(screen.getByLabelText('Channel 2 Microsoft Teams webhook URL'), 'https://contoso.webhook.office.com/x')
    await save(user)

    await screen.findByText('Notification settings saved.')
    const sent = lastSentChannels()
    expect(sent[0]).toMatchObject({ type: 'webhook', target: 'https://example.com/hook', secret: HMAC })
    expect(sent[1]).toMatchObject({ type: 'teams', secret: 'https://contoso.webhook.office.com/x' })
    expect(sent[1]).not.toHaveProperty('target')
    expect(within(channelGroup(2)).getByText('Channel 2 Microsoft Teams webhook URL:').parentElement).toHaveTextContent(
      'Set (contoso.webhook.office.com/…)',
    )
  })

  it('FR-9: server VALIDATION keyed `channels[i].field` is shown on that channel field', async () => {
    server.use(
      http.patch('*/api/projects/:id/settings/notifications', () =>
        problem(400, 'VALIDATION', 'The notification settings are invalid.', {
          errors: { 'channels[0].secret': ['Use the https:// incoming-webhook URL from Slack.'] },
        }),
      ),
    )
    const { user } = await openNotifications([slackChannel])

    await user.click(screen.getByRole('button', { name: 'Replace channel 1 Slack webhook URL' }))
    const input = screen.getByLabelText('New channel 1 Slack webhook URL')
    await user.type(input, NEW_SLACK_URL)
    await save(user)

    const link = await screen.findByRole('link', { name: 'Channel 1: Use the https:// incoming-webhook URL from Slack.' })
    expect(link).toHaveAttribute('href', `#${input.id}`)
    expect(input).toHaveAttribute('aria-invalid', 'true')
  })
})
