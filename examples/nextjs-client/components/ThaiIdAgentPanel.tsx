"use client";

import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { getCardReadToken, getFreshLocalAgentToken } from "@/lib/token-provider";
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
import type { MemberVerificationResponse } from "@/lib/member-verification";

type AgentStatus = "checking" | "ready" | "unavailable";

type Notice = { title: string; detail: string };

const baseUrl = process.env.NEXT_PUBLIC_THAI_ID_AGENT_BASE_URL ?? "https://localhost:18443";

/**
 * Operator console for the local Agent.
 *
 * The page initializes itself: health, readers, card status, and the SSE subscription all start on
 * mount. The manual controls in the Diagnostics section exist for troubleshooting and are never
 * required for normal use.
 */
export function ThaiIdAgentPanel() {
  const [agentStatus, setAgentStatus] = useState<AgentStatus>("checking");
  const [health, setHealth] = useState<AgentHealth | null>(null);
  const [readers, setReaders] = useState<SmartCardReaderInfo[]>([]);
  const [selectedReader, setSelectedReader] = useState<string>("");
  const [cardStatus, setCardStatus] = useState<SmartCardStatus | null>(null);
  const [cardPresent, setCardPresent] = useState(false);
  const [atr, setAtr] = useState<CardAtrResponse | null>(null);
  const [sseState, setSseState] = useState<SseConnectionState>("Disconnected");
  const [latestEvent, setLatestEvent] = useState<ReaderEvent | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [providerNotConfigured, setProviderNotConfigured] = useState(false);
  const [member, setMember] = useState<MemberVerificationResponse | null>(null);
  const [verifying, setVerifying] = useState(false);

  const mountedRef = useRef(true);
  const verifyingRef = useRef(false);
  const disconnectRef = useRef<(() => void) | null>(null);
  // Read inside SSE callbacks, which capture their closure once and would otherwise see a stale
  // selected reader for the lifetime of the subscription.
  const selectedReaderRef = useRef("");

  const client = useMemo(
    () => createThaiIdAgentClient({ baseUrl, timeoutMs: 10_000, getToken: getFreshLocalAgentToken }),
    [],
  );

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    selectedReaderRef.current = selectedReader;
  }, [selectedReader]);

  // ---- Data loading -----------------------------------------------------------------

  const checkAgent = useCallback(async () => {
    try {
      const result = await client.getHealth();
      if (!mountedRef.current) return;
      setHealth(result);
      setAgentStatus("ready");
      setNotice(null);
    } catch (error) {
      if (!mountedRef.current) return;
      setHealth(null);
      setAgentStatus("unavailable");
      setNotice(toNotice(error));
    }
  }, [client]);

  const loadReaders = useCallback(async () => {
    try {
      const result = await client.getReaders();
      if (!mountedRef.current) return;
      const nextReaders = result.data ?? [];
      setReaders(nextReaders);
      // Only auto-select when nothing is selected, or the selection disappeared. An operator's
      // explicit choice is never overridden by a background refresh.
      setSelectedReader((current) =>
        current && nextReaders.some((reader) => reader.name === current) ? current : nextReaders[0]?.name ?? "",
      );
      if (nextReaders.length === 0) {
        setCardPresent(false);
        setCardStatus(null);
      }
    } catch (error) {
      if (!mountedRef.current) return;
      setNotice(toNotice(error));
    }
  }, [client]);

  /**
   * Card presence is always read for the named reader. Reader-list flags are deliberately not used:
   * `readers.some(r => r.isCardPresent)` would report a card in a reader the operator did not
   * select, which is exactly the wrong answer on a multi-reader workstation.
   */
  const loadCardStatus = useCallback(
    async (readerName: string) => {
      if (!readerName) {
        setCardPresent(false);
        setCardStatus(null);
        return;
      }

      try {
        const result = await client.getCardStatus(readerName);
        // The selection may have changed while this request was in flight.
        if (!mountedRef.current || selectedReaderRef.current !== readerName) return;
        const status = result.data;
        setCardStatus(status);
        setCardPresent(status?.status === "CardPresent");
      } catch (error) {
        if (!mountedRef.current || selectedReaderRef.current !== readerName) return;
        setCardPresent(false);
        setNotice(toNotice(error));
      }
    },
    [client],
  );

  // ---- Automatic initialization -----------------------------------------------------

  // Each stage is started from a promise continuation rather than the effect's synchronous phase,
  // so no state is written while the effect is running — the updates land only once the Agent has
  // actually answered.

  // 1. Health on mount.
  useEffect(() => {
    Promise.resolve().then(() => checkAgent());
  }, [checkAgent]);

  // 2. Readers once the Agent answers.
  useEffect(() => {
    if (agentStatus !== "ready") return;
    Promise.resolve().then(() => loadReaders());
  }, [agentStatus, loadReaders]);

  // 3. Card status for whichever reader is selected.
  useEffect(() => {
    if (agentStatus !== "ready") return;
    Promise.resolve().then(() => loadCardStatus(selectedReader));
  }, [agentStatus, selectedReader, loadCardStatus]);

  // 4. SSE once the prerequisites hold. The subscription is agent-wide and is established once;
  //    changing the selected reader filters events rather than reconnecting.
  useEffect(() => {
    if (agentStatus !== "ready" || readers.length === 0) return;
    if (disconnectRef.current) return;

    disconnectRef.current = subscribeReaderEvents({
      baseUrl,
      getToken: getFreshLocalAgentToken,
      maxReconnects: 5,
      onStateChange: (state) => {
        if (mountedRef.current) setSseState(state);
      },
      onEvent: (event) => {
        if (!mountedRef.current) return;
        setLatestEvent(event);

        // Events for another reader must not move the selected reader's state.
        if (event.readerName !== selectedReaderRef.current) return;

        if (event.eventType === "CardRemoved") {
          setCardPresent(false);
          setAtr(null);
          setCardStatus(null);
          // The card left the reader: the next person at the counter must not see the previous
          // holder's result.
          setMember(null);
        }
        if (event.eventType === "CardInserted") {
          setCardPresent(true);
          void loadCardStatus(selectedReaderRef.current);
        }
      },
      onError: (error) => {
        if (mountedRef.current) setNotice(toNotice(error));
      },
    });

    return () => {
      disconnectRef.current?.();
      disconnectRef.current = null;
    };
  }, [agentStatus, readers.length, loadCardStatus]);

  // ---- Verification ------------------------------------------------------------------

  const agentReady = agentStatus === "ready";
  const hasReader = readers.length > 0 && selectedReader.length > 0;
  const canVerify = agentReady && hasReader && cardPresent && !verifying;

  async function verifyIdentity() {
    // Guard on a ref, not state: two clicks in the same tick would both read the stale value.
    if (verifyingRef.current || !canVerify) return;
    verifyingRef.current = true;
    setVerifying(true);
    setNotice(null);
    setProviderNotConfigured(false);
    setMember(null);

    try {
      // A token carrying `card.read` is minted only for this call.
      const readResult = await client.readCardIdentity(selectedReader, { getToken: getCardReadToken });
      const identity = readResult.data;
      if (!identity?.citizenId) {
        setNotice({ title: "อ่านบัตรไม่สำเร็จ", detail: "CARD_DATA_INVALID" });
        return;
      }

      const response = await fetch("/api/member/verify", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        cache: "no-store",
        body: JSON.stringify({ citizenId: identity.citizenId, readerName: identity.readerName }),
      });
      const payload = (await response.json()) as MemberVerificationResponse | { error?: string };

      if (!("outcome" in payload)) {
        setNotice({ title: "ตรวจสอบสมาชิกไม่สำเร็จ", detail: payload.error ?? "MEMBER_VERIFICATION_FAILED" });
        return;
      }

      setMember(payload);
      if (payload.outcome !== "MEMBER_MATCHED") {
        setNotice({ title: describeOutcome(payload.outcome), detail: payload.outcome });
      }
    } catch (error) {
      if (error instanceof AgentClientError && error.kind === "protocol-not-configured") {
        setProviderNotConfigured(true);
        return;
      }
      setNotice(toNotice(error));
    } finally {
      verifyingRef.current = false;
      if (mountedRef.current) setVerifying(false);
    }
  }

  async function readAtr() {
    setNotice(null);
    setAtr(null);
    try {
      const result = await client.readCardAtr(selectedReader || undefined);
      if (!mountedRef.current) return;
      const nextAtr = result.data;
      setAtr(nextAtr && normalizeAtr(nextAtr.atr) ? nextAtr : null);
    } catch (error) {
      if (mountedRef.current) setNotice(toNotice(error));
    }
  }

  const selected = readers.find((reader) => reader.name === selectedReader);

  return (
    <main className="shell">
      <section className="grid" aria-label="สถานะการยืนยันตัวตน">
        <div className="panel">
          <h1>ตรวจสอบการยืนยันตัวตนด้วยบัตรประชาชน</h1>
          <dl>
            <dt>Agent</dt>
            <dd className={agentStatus === "unavailable" ? "danger" : ""}>
              {agentStatus === "checking" ? "กำลังตรวจสอบ..." : agentReady ? "พร้อมใช้งาน" : "ไม่พร้อมใช้งาน"}
            </dd>

            <dt>เครื่องอ่านบัตร</dt>
            <dd className={agentReady && !hasReader ? "danger" : ""}>
              {!agentReady ? "-" : hasReader ? "พร้อมใช้งาน" : "ไม่พบเครื่องอ่าน"}
            </dd>

            <dt>ชื่อเครื่องอ่าน</dt>
            <dd>{selectedReader || "-"}</dd>

            <dt>บัตรประชาชน</dt>
            <dd className={hasReader && !cardPresent ? "danger" : ""}>
              {!hasReader ? "-" : cardPresent ? "พบบัตร พร้อมตรวจสอบ" : "กรุณาเสียบบัตร"}
            </dd>
          </dl>
        </div>

        <div className="panel">
          <h2>ดำเนินการ</h2>
          <button type="button" onClick={verifyIdentity} disabled={!canVerify} aria-busy={verifying}>
            {verifying ? "กำลังตรวจสอบ..." : "ตรวจสอบการยืนยันตัวตน"}
          </button>
          {verifying ? <p role="status">กำลังอ่านบัตรและตรวจสอบข้อมูลสมาชิก...</p> : null}
          {!canVerify && !verifying ? (
            <p className="muted">{verifyDisabledReason(agentStatus, hasReader, cardPresent)}</p>
          ) : null}

          {providerNotConfigured ? (
            <p role="alert" className="danger">
              <strong>ยังไม่ได้เชื่อมโมดูลอ่านข้อมูลบัตรประชาชน</strong>
              <span> ระบบตรวจสอบเครื่องอ่านและบัตรได้ แต่ยังอ่านข้อมูลในบัตรไม่ได้ (อยู่ระหว่างการพัฒนา/นำร่อง)</span>
            </p>
          ) : null}

          {notice ? (
            <p role="alert">
              <strong>{notice.title}</strong>
              <span> {notice.detail}</span>
            </p>
          ) : null}
        </div>

        {member ? (
          <div className="panel" aria-label="ผลการตรวจสอบสมาชิก">
            <h2>ผลการตรวจสอบสมาชิก</h2>
            {member.outcome === "MEMBER_MATCHED" ? (
              <dl>
                <dt>สถานะ</dt><dd>พบข้อมูลสมาชิก</dd>
                <dt>รหัสสมาชิก</dt><dd>{member.memberId}</dd>
                <dt>เลขที่สมาชิก</dt><dd>{member.memberNo}</dd>
                <dt>ชื่อ-สกุล</dt><dd>{member.fullName}</dd>
                <dt>ประเภทสมาชิก</dt><dd>{member.memberType ?? "-"}</dd>
                <dt>สถานะสมาชิก</dt><dd>{member.memberStatus ?? "-"}</dd>
                <dt>เลขบัตร (ปกปิด)</dt><dd className="mono">{member.maskedCitizenId ?? "-"}</dd>
                <dt>Verification ID</dt><dd className="mono">{member.verificationId}</dd>
              </dl>
            ) : (
              <p role="alert">
                <strong>{describeOutcome(member.outcome)}</strong>
              </p>
            )}
          </div>
        ) : null}
      </section>

      <details className="panel">
        <summary>Diagnostics / ขั้นสูง</summary>

        <section className="toolbar" aria-label="Manual diagnostics controls">
          <button type="button" onClick={() => void checkAgent()}>Check Agent</button>
          <button type="button" onClick={() => void loadReaders()}>Refresh Readers</button>
          <button type="button" onClick={() => void loadCardStatus(selectedReader)} disabled={!selectedReader}>
            Refresh Card Status
          </button>
          <button type="button" onClick={() => void readAtr()} disabled={!selectedReader}>Read ATR</button>
        </section>

        <label>
          Reader
          <select
            value={selectedReader}
            onChange={(event) => setSelectedReader(event.target.value)}
            disabled={readers.length === 0}
          >
            {readers.length === 0 ? <option value="">No reader</option> : null}
            {readers.map((reader) => (
              <option key={reader.name} value={reader.name}>{reader.name}</option>
            ))}
          </select>
        </label>

        <dl>
          <dt>Agent URL</dt><dd>{baseUrl}</dd>
          <dt>Health</dt><dd>{health ? `${health.status} (${health.version})` : "-"}</dd>
          <dt>Reader count</dt><dd>{readers.length}</dd>
          <dt>Reader connected</dt><dd>{selected ? String(selected.isConnected) : "-"}</dd>
          <dt>Status API</dt><dd>{cardStatus?.status ?? "-"}</dd>
          <dt>ATR</dt><dd className="mono">{atr?.atr ?? cardStatus?.atr ?? "-"}</dd>
          <dt>SSE state</dt><dd>{sseState}</dd>
          <dt>Latest event</dt><dd>{latestEvent?.eventType ?? "-"}</dd>
          <dt>Event reader</dt><dd>{latestEvent?.readerName ?? "-"}</dd>
        </dl>

        <p className="muted">
          ระบบอ่านเฉพาะเลขประจำตัวประชาชน 13 หลัก ไม่อ่านรูปถ่าย ที่อยู่ ชื่อ หรือวันเกิดจากบัตร
          และไม่เก็บเลขบัตรไว้ในเบราว์เซอร์
        </p>
      </details>
    </main>
  );
}

function verifyDisabledReason(agentStatus: AgentStatus, hasReader: boolean, cardPresent: boolean): string {
  if (agentStatus === "checking") return "กำลังตรวจสอบสถานะ Agent...";
  if (agentStatus === "unavailable") return "ไม่พบ Local Agent กรุณาตรวจสอบว่าบริการทำงานอยู่";
  if (!hasReader) return "ไม่พบเครื่องอ่านบัตร";
  if (!cardPresent) return "กรุณาเสียบบัตรประชาชน";
  return "";
}

function describeOutcome(outcome: MemberVerificationResponse["outcome"]): string {
  switch (outcome) {
    case "MEMBER_MATCHED":
      return "พบข้อมูลสมาชิก";
    case "MEMBER_NOT_FOUND":
      return "ไม่พบข้อมูลสมาชิกในฐานข้อมูลสหกรณ์";
    case "MEMBER_DUPLICATE":
      return "พบข้อมูลสมาชิกซ้ำซ้อน ต้องตรวจสอบด้วยเจ้าหน้าที่";
    case "MEMBER_DATABASE_UNAVAILABLE":
      return "ไม่สามารถเชื่อมต่อฐานข้อมูลสมาชิกได้";
    case "CITIZEN_ID_INVALID":
      return "ข้อมูลบัตรไม่ถูกต้อง";
  }
}

function toNotice(error: unknown): Notice {
  if (error instanceof AgentClientError) {
    if (error.kind === "protocol-not-configured") {
      return { title: "ยังไม่ได้เชื่อมโมดูลอ่านข้อมูลบัตรประชาชน", detail: error.code ?? "THAI_CARD_PROTOCOL_NOT_CONFIGURED" };
    }
    if (error.kind === "forbidden") {
      return { title: "ไม่มีสิทธิ์อ่านข้อมูลบัตร", detail: error.code ?? "FORBIDDEN" };
    }
    if (error.kind === "card-removed") {
      return { title: "บัตรถูกถอดออกระหว่างอ่าน", detail: error.code ?? "CARD_REMOVED_DURING_READ" };
    }
    if (error.kind === "tls-or-network") {
      return { title: "ไม่พบ Local Agent", detail: "ตรวจสอบว่าบริการ ThaiIdCardAgent ทำงานอยู่ และ HTTPS localhost ได้รับความไว้วางใจ" };
    }
    if (error.kind === "card-not-present") {
      return { title: "ไม่พบบัตรในเครื่องอ่าน", detail: error.code ?? "CARD_NOT_PRESENT" };
    }
    if (error.kind === "auth" || error.kind === "replay") {
      return { title: "การยืนยันสิทธิ์ล้มเหลว", detail: error.code ?? "UNAUTHORIZED" };
    }
    return { title: "Agent error", detail: error.code ?? "AGENT_ERROR" };
  }

  return { title: "เกิดข้อผิดพลาด", detail: "คำขอล้มเหลวโดยไม่เปิดเผยรายละเอียดภายใน" };
}
