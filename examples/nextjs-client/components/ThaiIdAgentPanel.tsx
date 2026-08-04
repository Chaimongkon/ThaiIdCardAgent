"use client";

import React, { useMemo, useRef, useState } from "react";
import { getFreshLocalAgentToken } from "@/lib/token-provider";
import {
  AgentClientError,
  AgentHealth,
  CardAtrResponse,
  SmartCardReaderInfo,
  SmartCardStatus,
  createThaiIdAgentClient,
  normalizeAtr,
} from "@/lib/thai-id-agent-client";
import type { ReaderEvent } from "@/lib/thai-id-agent-client";
import type { SseConnectionState } from "@/lib/sse";
import { subscribeReaderEvents } from "@/lib/sse";

type CardState = "NoReader" | "NoCard" | "CardPresent" | "Error";

type Notice = {
  title: string;
  detail: string;
};

const baseUrl = process.env.NEXT_PUBLIC_THAI_ID_AGENT_BASE_URL ?? "https://localhost:18443";

export function ThaiIdAgentPanel() {
  const [health, setHealth] = useState<AgentHealth | null>(null);
  const [readers, setReaders] = useState<SmartCardReaderInfo[]>([]);
  const [selectedReader, setSelectedReader] = useState<string>("");
  const [cardStatus, setCardStatus] = useState<SmartCardStatus | null>(null);
  const [cardState, setCardState] = useState<CardState>("NoReader");
  const [atr, setAtr] = useState<CardAtrResponse | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [eventsState, setEventsState] = useState<SseConnectionState>("Disconnected");
  const [latestEvent, setLatestEvent] = useState<ReaderEvent | null>(null);
  const disconnectRef = useRef<(() => void) | null>(null);

  const client = useMemo(
    () =>
      createThaiIdAgentClient({
        baseUrl,
        timeoutMs: 10_000,
        getToken: getFreshLocalAgentToken,
      }),
    [],
  );

  async function checkAgent() {
    setNotice(null);
    try {
      setHealth(await client.getHealth());
    } catch (error) {
      setNotice(toNotice(error));
    }
  }

  async function refreshReaders() {
    setNotice(null);
    setAtr(null);
    try {
      const result = await client.getReaders();
      const nextReaders = result.data ?? [];
      setReaders(nextReaders);
      const nextReader = selectedReader && nextReaders.some((reader) => reader.name === selectedReader) ? selectedReader : nextReaders[0]?.name ?? "";
      setSelectedReader(nextReader);
      setCardState(nextReaders.length === 0 ? "NoReader" : nextReaders.some((reader) => reader.isCardPresent) ? "CardPresent" : "NoCard");
    } catch (error) {
      setCardState("Error");
      setNotice(toNotice(error));
    }
  }

  async function refreshCardStatus() {
    setNotice(null);
    setAtr(null);
    try {
      const result = await client.getCardStatus(selectedReader || undefined);
      const status = result.data;
      setCardStatus(status);
      setCardState(status?.status === "CardPresent" ? "CardPresent" : status?.status === "NoCard" ? "NoCard" : "Error");
    } catch (error) {
      setCardState("Error");
      setNotice(toNotice(error));
    }
  }

  async function readAtr() {
    setNotice(null);
    setAtr(null);
    try {
      const result = await client.readCardAtr(selectedReader || undefined);
      const nextAtr = result.data;
      setAtr(nextAtr && normalizeAtr(nextAtr.atr) ? nextAtr : null);
      setCardState("CardPresent");
    } catch (error) {
      setNotice(toNotice(error));
    }
  }

  function connectEvents() {
    if (disconnectRef.current) return;
    setNotice(null);
    setLatestEvent(null);
    disconnectRef.current = subscribeReaderEvents({
      baseUrl,
      getToken: getFreshLocalAgentToken,
      maxReconnects: 5,
      onStateChange: setEventsState,
      onEvent: (event) => {
        setLatestEvent(event);
        if (event.eventType === "CardRemoved") {
          setAtr(null);
          setCardState("NoCard");
        }
        if (event.eventType === "CardInserted") {
          setCardState("CardPresent");
        }
      },
      onError: (error) => setNotice(toNotice(error)),
    });
  }

  function disconnectEvents() {
    disconnectRef.current?.();
    disconnectRef.current = null;
    setEventsState("Disconnected");
  }

  const selected = readers.find((reader) => reader.name === selectedReader);

  return (
    <main className="shell">
      <section className="toolbar" aria-label="ThaiIdCardAgent controls">
        <button type="button" onClick={checkAgent}>Check Agent</button>
        <button type="button" onClick={refreshReaders}>Refresh Readers</button>
        <button type="button" onClick={refreshCardStatus} disabled={!selectedReader}>Refresh Card Status</button>
        <button type="button" onClick={readAtr} disabled={!selectedReader}>Read ATR</button>
        <button type="button" onClick={connectEvents} disabled={eventsState === "Connected" || eventsState === "Connecting" || eventsState === "Reconnecting"}>Connect Events</button>
        <button type="button" onClick={disconnectEvents}>Disconnect Events</button>
        <button type="button" disabled title="Thai card protocol is not configured.">Card Read</button>
      </section>

      <section className="grid" aria-label="Agent integration status">
        <div className="panel">
          <h1>ThaiIdCardAgent Web Integration</h1>
          <dl>
            <dt>Agent URL</dt><dd>{baseUrl}</dd>
            <dt>Health</dt><dd>{health ? `${health.status} (${health.version})` : "Not checked"}</dd>
            <dt>Reader count</dt><dd>{readers.length}</dd>
            <dt>Card state</dt><dd className={cardState === "Error" ? "danger" : ""}>{cardState}</dd>
          </dl>
        </div>

        <div className="panel">
          <h2>Reader</h2>
          <label>
            Reader
            <select value={selectedReader} onChange={(event) => setSelectedReader(event.target.value)} disabled={readers.length === 0}>
              {readers.length === 0 ? <option value="">No reader</option> : null}
              {readers.map((reader) => <option key={reader.name} value={reader.name}>{reader.name}</option>)}
            </select>
          </label>
          <dl>
            <dt>Connected</dt><dd>{selected ? String(selected.isConnected) : "-"}</dd>
            <dt>Card present</dt><dd>{selected ? String(selected.isCardPresent) : "-"}</dd>
            <dt>Status API</dt><dd>{cardStatus?.status ?? "-"}</dd>
            <dt>ATR</dt><dd className="mono">{atr?.atr ?? cardStatus?.atr ?? selected?.atr ?? "-"}</dd>
          </dl>
        </div>

        <div className="panel">
          <h2>Events</h2>
          <dl>
            <dt>SSE state</dt><dd>{eventsState}</dd>
            <dt>Latest event</dt><dd>{latestEvent?.eventType ?? "-"}</dd>
            <dt>Reader</dt><dd>{latestEvent?.readerName ?? "-"}</dd>
            <dt>Occurred</dt><dd>{latestEvent?.occurredAtUtc ?? "-"}</dd>
            <dt>ATR</dt><dd className="mono">{latestEvent?.atr ?? "-"}</dd>
          </dl>
        </div>

        <div className="panel">
          <h2>Diagnostics</h2>
          {notice ? <p role="alert"><strong>{notice.title}</strong><span>{notice.detail}</span></p> : <p>No current error.</p>}
          <p className="muted">Card personal data is not read. The Thai card protocol endpoint remains disabled.</p>
        </div>
      </section>
    </main>
  );
}

function toNotice(error: unknown): Notice {
  if (error instanceof AgentClientError) {
    if (error.kind === "tls-or-network") {
      return { title: "Agent unavailable or certificate trust failed", detail: "Check that the ThaiIdCardAgent service is running with Get-Service ThaiIdCardAgent and that localhost HTTPS is trusted." };
    }
    if (error.kind === "card-not-present") {
      return { title: "No card present", detail: error.code ?? "CARD_NOT_PRESENT" };
    }
    if (error.kind === "auth" || error.kind === "replay") {
      return { title: "Authentication failed", detail: error.code ?? "UNAUTHORIZED" };
    }
    return { title: "Agent error", detail: error.code ?? error.message };
  }

  return { title: "Unexpected error", detail: "The request failed without exposing sensitive details." };
}


