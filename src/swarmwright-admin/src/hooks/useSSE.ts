import { useEffect, useRef, useState, useCallback } from 'react';
import type { SwarmEvent } from '../types/swarm';
import { devLog } from '../lib/devLog';

const log = devLog('sse');

const INITIAL_RECONNECT_DELAY = 1000;
const MAX_RECONNECT_DELAY = 30000;

interface UseSSEOptions {
  url: string;
  onEvent: (event: SwarmEvent) => void;
  getToken: () => Promise<string | null>;
  enabled?: boolean;
}

export function useSSE(options: UseSSEOptions) {
  const { url, onEvent, getToken, enabled = true } = options;
  const [connected, setConnected] = useState(false);
  const onEventRef = useRef(onEvent);
  const getTokenRef = useRef(getToken);
  const abortRef = useRef<AbortController | null>(null);

  // Keep callback refs fresh without triggering reconnects
  onEventRef.current = onEvent;
  getTokenRef.current = getToken;

  const disconnect = useCallback(() => {
    if (abortRef.current) {
      abortRef.current.abort();
      abortRef.current = null;
    }
    setConnected(false);
  }, []);

  useEffect(() => {
    if (!enabled) {
      log.debug('disabled (skipping connect)', url);
      return;
    }

    let active = true;
    let reconnectDelay = INITIAL_RECONNECT_DELAY;

    async function connect() {
      if (!active) return;

      const controller = new AbortController();
      abortRef.current = controller;

      try {
        const token = await getTokenRef.current();
        if (!active) return;

        const headers: Record<string, string> = {
          Accept: 'text/event-stream',
        };
        if (token) {
          headers['Authorization'] = `Bearer ${token}`;
        }

        const response = await fetch(url, {
          headers,
          signal: controller.signal,
        });

        if (!response.ok) {
          throw new Error(`SSE connection failed: ${response.status}`);
        }

        if (!response.body) {
          throw new Error('Response body is null');
        }

        log.info(`connected`, url);
        setConnected(true);
        reconnectDelay = INITIAL_RECONNECT_DELAY; // Reset on successful connect

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (active) {
          const { done, value } = await reader.read();

          if (done) {
            log.info('stream ended', url);
            break;
          }

          buffer += decoder.decode(value, { stream: true });
          const messages = buffer.split('\n\n');
          buffer = messages.pop() || ''; // keep incomplete message in buffer

          for (const msg of messages) {
            if (!msg.trim()) continue;
            const dataLine = msg.split('\n').find((l) => l.startsWith('data: '));
            if (dataLine) {
              const json = dataLine.slice(6); // remove 'data: ' prefix
              try {
                const event: SwarmEvent = JSON.parse(json);
                log.info(`<- ${event.type}`, event);
                onEventRef.current(event);
              } catch {
                // Ignore malformed messages (heartbeats, etc.)
              }
            }
          }
        }
      } catch (err) {
        if (!active) return;
        if ((err as Error).name === 'AbortError') return;
        log.warn(`connection error`, url, err);
      }

      setConnected(false);

      // Reconnect with exponential backoff
      if (active) {
        log.info(`reconnecting in ${reconnectDelay}ms`, url);
        await new Promise((resolve) => setTimeout(resolve, reconnectDelay));
        reconnectDelay = Math.min(reconnectDelay * 2, MAX_RECONNECT_DELAY);
        connect();
      }
    }

    connect();

    return () => {
      active = false;
      if (abortRef.current) {
        abortRef.current.abort();
        abortRef.current = null;
      }
      setConnected(false);
    };
  }, [url, enabled]);

  return { connected, disconnect };
}
