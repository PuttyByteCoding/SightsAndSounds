import { render, screen } from '@testing-library/svelte';
import { describe, expect, test } from 'vitest';
import FfprobeResultModal from './FfprobeResultModal.svelte';
import type { FfprobeResult } from '$lib/types';

const ok: FfprobeResult = {
  stdout: '{"format":{"duration":"2.0"}}',
  stderr: '',
  exitCode: 0,
  filePath: '/movies/clip.mp4',
};

describe('FfprobeResultModal', () => {
  test('is closed when result is null', () => {
    render(FfprobeResultModal, { props: { result: null } });
    expect(screen.queryByText('ffprobe diagnostics')).not.toBeInTheDocument();
  });

  test('opens and shows the path + a success exit badge', () => {
    render(FfprobeResultModal, { props: { result: ok } });
    expect(screen.getByText('ffprobe diagnostics')).toBeInTheDocument();
    expect(screen.getByText('/movies/clip.mp4')).toBeInTheDocument();
    expect(screen.getByText('exit 0')).toBeInTheDocument();
  });

  test('surfaces stderr and a non-zero exit code on failure', () => {
    const bad: FfprobeResult = {
      stdout: '',
      stderr: 'moov atom not found',
      exitCode: 1,
      filePath: '/movies/broken.mp4',
    };
    render(FfprobeResultModal, { props: { result: bad } });
    expect(screen.getByText('exit 1')).toBeInTheDocument();
    expect(screen.getByText('moov atom not found')).toBeInTheDocument();
  });
});
