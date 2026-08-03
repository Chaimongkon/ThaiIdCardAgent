"use client";

import { useEffect, useMemo, useState } from "react";
import {
  AgentCredential,
  AgentHttpError,
  CardAtrResponse,
  SmartCardReaderInfo,
  getAgentHealth,
  getReaders,
  readCardAtr,
  readThaiIdCard,
  subscribeReaderEvents,
} from "./thai-id-agent-client";

type Props = {
  tokenOrDevelopmentKey: AgentCredential | null;
  baseUrl?: string;
  isOpen?: boolean;
};

type UiState =
  | "checking-agent"
  | "agent-not-found"
  | "no-reader"
  | "insert-card"
  | "ready"
  | "reading"
  | "atr-success"
  | "thai-card-read-not-supported"
  | "error";

export function ThaiIdAgentExample({ tokenOrDevelopmentKey, baseUrl = "https://127.0.0.1:18443", isOpen = true }: Props) {
  const [state, setState] = useState<UiState>("checking-agent");
  const [readers, setReaders] = useState<SmartCardReaderInfo[]>([]);
  const [selectedReader, setSelectedReader] = useState<string>("");
  const [atrResult, setAtrResult] = useState<CardAtrResponse | null>(null);
  const [errorMessage, setErrorMessage] = useState<string>("");

  const hasCredential = useMemo(() => tokenOrDevelopmentKey !== null, [tokenOrDevelopmentKey]);

  useEffect(() => {
    const controller = new AbortController();
    resetSensitiveState();

    async function load() {
      setState("checking-agent");
      await getAgentHealth({ baseUrl, signal: controller.signal });
      if (!tokenOrDevelopmentKey) {
        setState("error");
        setErrorMessage("ไม่พบสิทธิ์เรียก Agent");
        return;
      }

      const result = await getReaders(tokenOrDevelopmentKey, { baseUrl, signal: controller.signal });
      const availableReaders = result.data ?? [];
      setReaders(availableReaders);
      setSelectedReader(availableReaders[0]?.name ?? "");
      if (availableReaders.length === 0) setState("no-reader");
      else if (!availableReaders.some((reader) => reader.isCardPresent)) setState("insert-card");
      else setState("ready");
    }

    load().catch((error) => {
      if (controller.signal.aborted) return;
      setState(error instanceof TypeError ? "agent-not-found" : "error");
      setErrorMessage(error instanceof AgentHttpError ? `HTTP ${error.status}` : "เกิดข้อผิดพลาด");
    });

    return () => {
      controller.abort();
      resetSensitiveState();
    };
  }, [baseUrl, hasCredential, tokenOrDevelopmentKey]);

  useEffect(() => {
    if (!tokenOrDevelopmentKey || !isOpen) return;
    const unsubscribe = subscribeReaderEvents(
      tokenOrDevelopmentKey,
      {
        onEvent: (event) => {
          if (event.eventType === "CardRemoved") {
            setAtrResult(null);
            setState("insert-card");
          }
          if (event.eventType === "CardInserted") setState("ready");
        },
        onError: () => setState("error"),
      },
      { baseUrl },
    );

    return unsubscribe;
  }, [baseUrl, isOpen, tokenOrDevelopmentKey]);

  useEffect(() => {
    if (!isOpen) resetSensitiveState();
  }, [isOpen]);

  async function handleReadAtr() {
    if (!tokenOrDevelopmentKey || !selectedReader) return;
    setAtrResult(null);
    setErrorMessage("");
    setState("reading");
    try {
      const result = await readCardAtr(tokenOrDevelopmentKey, selectedReader, { baseUrl });
      setAtrResult(result.data ?? null);
      setState("atr-success");
    } catch (error) {
      setState("error");
      setErrorMessage(error instanceof AgentHttpError ? `HTTP ${error.status}` : "เกิดข้อผิดพลาด");
    }
  }

  async function handleReadThaiIdCard() {
    if (!tokenOrDevelopmentKey || !selectedReader) return;
    resetSensitiveState();
    setState("reading");
    try {
      await readThaiIdCard(tokenOrDevelopmentKey, { readCitizenId: false, readThaiName: false }, selectedReader, { baseUrl });
    } catch (error) {
      if (error instanceof AgentHttpError && JSON.stringify(error.response).includes("THAI_CARD_PROTOCOL_NOT_CONFIGURED")) {
        setState("thai-card-read-not-supported");
        return;
      }

      setState("error");
      setErrorMessage("เกิดข้อผิดพลาด");
    }
  }

  function resetSensitiveState() {
    setAtrResult(null);
    setErrorMessage("");
  }

  function closeModal() {
    resetSensitiveState();
  }

  return (
    <section aria-label="Thai ID card agent">
      <div>{labelForState(state)}</div>
      {errorMessage ? <p role="alert">{errorMessage}</p> : null}

      <select value={selectedReader} onChange={(event) => setSelectedReader(event.target.value)} disabled={readers.length === 0}>
        {readers.map((reader) => (
          <option key={reader.name} value={reader.name}>
            {reader.name} {reader.isCardPresent ? "พร้อมใช้งาน" : "กรุณาเสียบบัตร"}
          </option>
        ))}
      </select>

      <button type="button" onClick={handleReadAtr} disabled={!selectedReader || state === "reading"}>
        อ่าน ATR
      </button>
      <button type="button" onClick={handleReadThaiIdCard} disabled={!selectedReader || state === "reading"}>
        ตรวจการอ่านบัตร
      </button>
      <button type="button" onClick={closeModal}>
        ปิด
      </button>

      {atrResult ? <output>ATR: {atrResult.atr}</output> : null}
    </section>
  );
}

function labelForState(state: UiState): string {
  switch (state) {
    case "checking-agent":
      return "กำลังตรวจสอบ Agent";
    case "agent-not-found":
      return "ไม่พบ Agent";
    case "no-reader":
      return "ไม่พบเครื่องอ่าน";
    case "insert-card":
      return "กรุณาเสียบบัตร";
    case "ready":
      return "พร้อมใช้งาน";
    case "reading":
      return "กำลังอ่าน";
    case "atr-success":
      return "อ่าน ATR สำเร็จ";
    case "thai-card-read-not-supported":
      return "ยังไม่รองรับการอ่านข้อมูลบัตรประชาชนไทย";
    case "error":
      return "เกิดข้อผิดพลาด";
  }
}
