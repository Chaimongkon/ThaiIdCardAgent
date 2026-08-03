"use client";

import { useEffect, useState } from "react";
import { getAgentHealth, getReaders, readCardAtr, SmartCardReaderInfo } from "./thai-id-agent-client";

type Props = {
  token: string;
};

export function ThaiIdAgentExample({ token }: Props) {
  const [readers, setReaders] = useState<SmartCardReaderInfo[]>([]);
  const [message, setMessage] = useState<string>("");
  const [atr, setAtr] = useState<string>("");

  useEffect(() => {
    let disposed = false;
    async function load() {
      setAtr("");
      const health = await getAgentHealth();
      if (disposed) return;
      setMessage(health.status);
      const result = await getReaders(token);
      if (disposed) return;
      setReaders(result.data ?? []);
    }

    load().catch(() => {
      if (!disposed) setMessage("agent_unavailable");
    });

    return () => {
      disposed = true;
      setReaders([]);
      setAtr("");
      setMessage("");
    };
  }, [token]);

  async function readAtr(readerName: string) {
    setAtr("");
    const result = await readCardAtr(token, readerName);
    setAtr(result.data?.atr ?? "");
  }

  return (
    <section>
      <h2>Thai ID Agent</h2>
      <p>{message}</p>
      <ul>
        {readers.map((reader) => (
          <li key={reader.name}>
            <button type="button" onClick={() => readAtr(reader.name)}>
              {reader.name} {reader.isCardPresent ? "card present" : "no card"}
            </button>
          </li>
        ))}
      </ul>
      {atr ? <output>{atr}</output> : null}
    </section>
  );
}