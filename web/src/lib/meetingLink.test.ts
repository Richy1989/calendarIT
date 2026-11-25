import { describe, expect, it } from 'vitest'
import { findMeetingLink } from './meetingLink'

describe('findMeetingLink', () => {
  it('finds the common services', () => {
    expect(findMeetingLink('https://us02web.zoom.us/j/81234567890?pwd=abc')).toEqual({
      url: 'https://us02web.zoom.us/j/81234567890?pwd=abc',
      service: 'Zoom',
    })
    expect(findMeetingLink('Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_x/0?context=%7b%7d')?.service).toBe('Teams')
    expect(findMeetingLink('https://meet.google.com/abc-defg-hij')?.service).toBe('Google Meet')
    expect(findMeetingLink('https://meet.jit.si/OurStandup')?.service).toBe('Jitsi')
    expect(findMeetingLink('https://acme.webex.com/meet/jane')?.service).toBe('Webex')
    expect(findMeetingLink('https://whereby.com/team-room')?.service).toBe('Whereby')
  })

  it('looks through the location first, then the description', () => {
    const link = findMeetingLink('Room 4', 'Dial in or https://meet.google.com/abc-defg-hij.')
    expect(link?.url).toBe('https://meet.google.com/abc-defg-hij')
    expect(findMeetingLink('https://meet.jit.si/a', 'https://meet.google.com/abc-defg-hij')?.service).toBe('Jitsi')
  })

  it('ignores anything that only looks like a meeting', () => {
    for (const text of [
      'http://zoom.us/j/123',                    // not https
      'https://zoom.us.evil.example/j/123',      // another host
      'https://evilzoom.us/j/123',
      'https://zoom.us/pricing',                 // no meeting path
      'https://meet.google.com/',
      'https://user:pw@zoom.us/j/123',           // credentials in the link
      'javascript:alert(1)//https://zoom.us/j/1x', // the match starts at https, but…
      'Office, 2nd floor',
      '',
    ]) {
      const link = findMeetingLink(text)
      if (link) expect(link.url.startsWith('https://zoom.us/j/1x'), text).toBe(true)
      else expect(link, text).toBeNull()
    }
  })
})
