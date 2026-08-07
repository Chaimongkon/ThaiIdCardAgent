"use client";

/**
 * Development-only manual member verification harness.
 *
 * The official Thai card provider is blocked (`BLOCKED_OFFICIAL_PROTOCOL_REQUIRED`), so no physical
 * card can be read. This panel substitutes a scenario picker for the card read so the rest of the
 * flow — the verification route, the member directory, and the Member Card — can be exercised and
 * demonstrated end to end.
 *
 * **It never reads a card and never touches the local agent.** It posts a synthetic citizen ID to
 * the example verification route and renders what comes back.
 *
 * Privacy: the citizen ID is never held in component state. The picker stores a scenario key, and
 * the identifier is read from a module constant inside the submit handler only — so it cannot reach
 * a URL, `localStorage`, `sessionStorage`, the console, or a React DevTools state dump.
 */

import React, { useRef, useState } from "react";
import type { MemberVerificationResponse } from "@/lib/member-verification";
import { SyntheticCitizenIds } from "@/lib/member-directory-mock";

export const DEV_BANNER_TEXT = "ข้อมูลจำลองสำหรับการพัฒนา — ไม่ได้อ่านจากบัตรจริง";

type ScenarioKey = "matched" | "notFound" | "duplicate" | "resigned";

type Scenario = {
  key: ScenarioKey;
  label: string;
  description: string;
  /** Read only inside the submit handler; never stored in state. */
  citizenId: string;
};

const scenarios: readonly Scenario[] = [
  {
    key: "matched",
    label: "พบข้อมูลสมาชิก (สมาชิกปกติ)",
    description: "ยืนยันตัวตนสำเร็จ และสถานะสมาชิกปกติ",
    citizenId: SyntheticCitizenIds.activeMember,
  },
  {
    key: "notFound",
    label: "ไม่พบข้อมูลสมาชิก",
    description: "เลขบัตรถูกต้องตามรูปแบบ แต่ไม่มีในฐานข้อมูลสมาชิก",
    citizenId: SyntheticCitizenIds.unknownMember,
  },
  {
    key: "duplicate",
    label: "ข้อมูลซ้ำซ้อน (fail closed)",
    description: "พบสมาชิกมากกว่า 1 รายการ ระบบจะไม่เลือกรายการใดรายการหนึ่ง",
    citizenId: SyntheticCitizenIds.duplicatedMember,
  },
  {
    key: "resigned",
    label: "สมาชิกลาออก",
    description: "ยืนยันตัวตนสำเร็จ แต่สถานะสมาชิกคือลาออก",
    citizenId: SyntheticCitizenIds.inactiveMember,
  },
] as const;

type Notice = { title: string; detail: string };

export function DevMemberVerificationPanel() {
  const [scenarioKey, setScenarioKey] = useState<ScenarioKey>("matched");
  const [member, setMember] = useState<MemberVerificationResponse | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [verifying, setVerifying] = useState(false);
  const verifyingRef = useRef(false);

  const selectedScenario = scenarios.find((scenario) => scenario.key === scenarioKey) ?? scenarios[0];

  async function runVerification() {
    // Guard on a ref, not state: two clicks in one tick would both read the stale state value.
    if (verifyingRef.current) return;
    verifyingRef.current = true;
    setVerifying(true);
    setNotice(null);
    setMember(null);

    try {
      // The identifier is resolved here and passed straight to fetch. It is never assigned to state.
      const citizenId = selectedScenario.citizenId;
      const response = await fetch("/api/member-verification/id-card", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json" },
        cache: "no-store",
        body: JSON.stringify({ citizenId, readerName: "SIMULATED-DEV-READER" }),
      });
      const payload = (await response.json()) as MemberVerificationResponse | { error?: string };

      if (!("outcome" in payload)) {
        setNotice({ title: "ตรวจสอบไม่สำเร็จ", detail: payload.error ?? "MEMBER_VERIFICATION_FAILED" });
        return;
      }

      setMember(payload);
      if (payload.outcome !== "MEMBER_MATCHED") {
        setNotice({ title: describeOutcome(payload.outcome), detail: payload.outcome });
      }
    } catch {
      setNotice({ title: "ตรวจสอบไม่สำเร็จ", detail: "NETWORK_ERROR" });
    } finally {
      verifyingRef.current = false;
      setVerifying(false);
    }
  }

  /** Simulates the SSE CardRemoved event: the counter clears when the card leaves the reader. */
  function simulateCardRemoved() {
    setMember(null);
    setNotice({ title: "จำลองการถอดบัตร", detail: "CardRemoved — ล้างข้อมูลบนหน้าจอแล้ว" });
  }

  const matched = member?.outcome === "MEMBER_MATCHED";

  return (
    <main className="shell">
      <section
        role="alert"
        aria-label="Development mock data banner"
        style={{
          background: "#7f1d1d",
          color: "#fff",
          padding: "0.9rem 1.1rem",
          borderRadius: "0.5rem",
          fontWeight: 700,
          marginBottom: "1rem",
          border: "2px solid #fca5a5",
        }}
      >
        {DEV_BANNER_TEXT}
        <div style={{ fontWeight: 400, fontSize: "0.9rem", marginTop: "0.35rem" }}>
          หน้านี้ใช้ได้เฉพาะโหมดพัฒนา (<code>npm run dev</code>) และเรียก
          <code> /api/member-verification/id-card</code> ซึ่งเป็น EXAMPLE_ONLY_NOT_FOR_PRODUCTION
        </div>
      </section>

      <section className="toolbar" aria-label="Mock verification controls">
        <label>
          กรณีทดสอบ
          <select
            value={scenarioKey}
            onChange={(event) => setScenarioKey(event.target.value as ScenarioKey)}
            disabled={verifying}
          >
            {scenarios.map((scenario) => (
              <option key={scenario.key} value={scenario.key}>
                {scenario.label}
              </option>
            ))}
          </select>
        </label>
        <button type="button" onClick={runVerification} disabled={verifying} aria-busy={verifying}>
          {verifying ? "กำลังตรวจสอบ..." : "จำลองการอ่านบัตรและตรวจสอบสมาชิก"}
        </button>
        <button type="button" onClick={simulateCardRemoved}>
          จำลองการถอดบัตร (CardRemoved)
        </button>
      </section>

      <section className="grid" aria-label="Mock verification result">
        <div className="panel">
          <h1>ตรวจสอบสมาชิกด้วยบัตรประชาชน (จำลอง)</h1>
          <p className="muted">{selectedScenario.description}</p>
          {verifying ? <p role="status">กำลังตรวจสอบข้อมูลสมาชิก...</p> : null}
          {member === null && !verifying ? <p className="muted">ยังไม่ได้ตรวจสอบ</p> : null}
        </div>

        {member ? (
          <div className="panel" aria-label="Member card">
            <h2>บัตรสมาชิก</h2>

            {/* Identity matching and transaction eligibility are separate questions. This tool
                answers only the first; conflating them would let staff read "identity matched" as
                "cleared to transact", which is exactly the wrong inference for a resigned member. */}
            <dl>
              <dt>การยืนยันตัวตน</dt>
              <dd className={matched ? "" : "danger"}>
                {matched ? "✓ ตรงกับข้อมูลสมาชิก" : `✗ ${describeOutcome(member.outcome)}`}
              </dd>
              <dt>สิทธิ์ในการทำธุรกรรม</dt>
              <dd className="danger">⚠ ระบบนี้ไม่ได้ตัดสินสิทธิ์ — เจ้าหน้าที่ต้องตรวจสอบแยกต่างหาก</dd>
            </dl>

            {matched ? (
              <dl>
                <dt>รหัสสมาชิก</dt><dd>{member.memberId}</dd>
                <dt>เลขที่สมาชิก</dt><dd>{member.memberNo}</dd>
                <dt>ชื่อ-สกุล</dt><dd>{member.fullName}</dd>
                <dt>ประเภทสมาชิก</dt><dd>{member.memberType ?? "-"}</dd>
                <dt>สถานะสมาชิก</dt><dd className={isRestrictedStatus(member.memberStatus) ? "danger" : ""}>{member.memberStatus ?? "-"}</dd>
                {/* A photo reference is an identifier into the cooperative system, never image bytes. */}
                <dt>รหัสรูปถ่าย</dt><dd className="mono">{member.photoReference ?? "-"}</dd>
                <dt>เลขบัตร (ปกปิด)</dt><dd className="mono">{member.maskedCitizenId ?? "-"}</dd>
                <dt>Verification ID</dt><dd className="mono">{member.verificationId}</dd>
              </dl>
            ) : null}

            {matched && isRestrictedStatus(member.memberStatus) ? (
              <p role="alert" className="danger">
                <strong>ยืนยันตัวตนได้ แต่สถานะสมาชิกไม่ปกติ</strong>
                <span> ต้องตรวจสอบสิทธิ์ก่อนทำธุรกรรม</span>
              </p>
            ) : null}

            {!matched ? (
              <p className="muted">ไม่แสดงข้อมูลสมาชิกเมื่อยืนยันตัวตนไม่สำเร็จ</p>
            ) : null}
          </div>
        ) : null}

        <div className="panel">
          <h2>สถานะระบบ</h2>
          {notice ? (
            <p role="alert"><strong>{notice.title}</strong><span> {notice.detail}</span></p>
          ) : (
            <p>ไม่มีข้อผิดพลาด</p>
          )}
          <p className="muted">
            เลขบัตรประชาชนถูกส่งใน request body เท่านั้น ไม่ถูกเก็บใน URL, localStorage, sessionStorage
            หรือ console และไม่ถูกเก็บไว้ใน state ของหน้าจอนี้
          </p>
          <p className="muted">
            การอ่านบัตรจริงยังถูกบล็อกอยู่ (BLOCKED_OFFICIAL_PROTOCOL_REQUIRED) — Windows Service
            ยังใช้ NotConfiguredThaiCardDataProvider
          </p>
        </div>
      </section>
    </main>
  );
}

/** Statuses that mean "matched, but do not assume this person may transact". */
function isRestrictedStatus(status: string | null): boolean {
  if (!status) return false;
  return status !== "ปกติ";
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
