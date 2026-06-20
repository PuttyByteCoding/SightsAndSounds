import { render, screen } from '@testing-library/svelte';
import { describe, expect, test } from 'vitest';
import FileInfoPanel from './FileInfoPanel.svelte';
import type { Video } from '$lib/types';

// Minimal video — only the fields FileInfoPanel reads matter; the rest of the
// Video contract isn't exercised here.
const video = {
  fileName: 'holiday.mp4',
  filePath: '/movies/holiday.mp4',
  fileSize: 1048576, // 1.00 MB
  videoCodec: 'h264',
  width: 1920,
  height: 1080,
} as unknown as Video;

describe('FileInfoPanel', () => {
  test('renders the file metadata when shown', () => {
    render(FileInfoPanel, { props: { show: true, video } });

    expect(screen.getByText('File Info')).toBeInTheDocument();
    expect(screen.getByText('File Name')).toBeInTheDocument();
    expect(screen.getByText('holiday.mp4')).toBeInTheDocument();
    expect(screen.getByText('/movies/holiday.mp4')).toBeInTheDocument();
    // formatBytes(1048576) -> "1.00 MB"
    expect(screen.getByText('1.00 MB')).toBeInTheDocument();
  });

  test('renders nothing when hidden', () => {
    render(FileInfoPanel, { props: { show: false, video } });
    expect(screen.queryByText('File Info')).not.toBeInTheDocument();
  });

  test('renders nothing when there is no video', () => {
    render(FileInfoPanel, { props: { show: true, video: null } });
    expect(screen.queryByText('File Info')).not.toBeInTheDocument();
  });
});
