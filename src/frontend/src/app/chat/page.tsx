'use client';

import React, { useState, useRef, useEffect, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useTenantContext } from '../../contexts/TenantContext';
import { askCustomerChat } from '../../lib/api';
import type { AiUsage } from '../../lib/types';

interface ChatMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  refused?: boolean;
  usage?: AiUsage;
  timestamp: Date;
}

const SUGGESTED_QUESTIONS = [
  'What is in my cloud environment right now?',
  'Are there any critical security findings I should look at?',
  'What changed in my environment this week?',
  'Where can I save money on cloud spend?',
];

export default function ChatPage() {
  const { tenantId, currentTenant } = useTenantContext();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [conversationId] = useState(() => crypto.randomUUID());
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const sendMessage = useCallback(
    async (text?: string) => {
      const message = (text ?? input).trim();
      if (!message || !tenantId || isLoading) return;

      const userMsg: ChatMessage = {
        id: crypto.randomUUID(),
        role: 'user',
        content: message,
        timestamp: new Date(),
      };
      setMessages((prev) => [...prev, userMsg]);
      setInput('');
      setIsLoading(true);

      try {
        const response = await askCustomerChat(tenantId, { message, conversationId });
        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: 'assistant',
          content: response.response,
          refused: response.refused,
          usage: response.usage,
          timestamp: new Date(),
        };
        setMessages((prev) => [...prev, assistantMsg]);
      } catch (err: any) {
        const errorMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: 'system',
          content: `Error: ${err.message || 'Failed to get a response. Please try again.'}`,
          timestamp: new Date(),
        };
        setMessages((prev) => [...prev, errorMsg]);
      } finally {
        setIsLoading(false);
        inputRef.current?.focus();
      }
    },
    [input, tenantId, isLoading, conversationId]
  );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  };

  if (!tenantId) {
    return <div className="text-center py-20 text-gray-500">Select a workspace to use Assistant.</div>;
  }

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)]">
      {/* Header */}
      <div className="mb-4">
        <h1 className="text-xl font-bold">Assistant</h1>
        <p className="text-sm text-gray-500 mt-1">
          Answers about {currentTenant?.displayName}&apos;s cloud workspace, grounded in your own data.
          The Assistant is read-only — it cannot change anything.
        </p>
      </div>

      {/* Chat area */}
      <div className="flex-1 bg-white rounded-xl shadow-sm border border-gray-200 flex flex-col overflow-hidden">
        <div className="flex-1 overflow-y-auto p-5 space-y-4">
          {messages.length === 0 && (
            <div className="flex flex-col items-center justify-center h-full text-center">
              <div className="w-12 h-12 bg-azure-100 rounded-full flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-azure-600" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09Z" />
                </svg>
              </div>
              <h3 className="text-lg font-semibold text-gray-700 mb-2">Ask about your workspace</h3>
              <p className="text-sm text-gray-500 mb-6 max-w-md">
                I answer only from your workspace&apos;s live data. If I don&apos;t know, I&apos;ll say so —
                I never guess.
              </p>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2 max-w-2xl w-full">
                {SUGGESTED_QUESTIONS.map((q, i) => (
                  <button
                    key={i}
                    onClick={() => sendMessage(q)}
                    className="text-left text-sm px-4 py-3 rounded-lg border border-gray-200 hover:border-azure-300 hover:bg-azure-50 transition-colors"
                  >
                    {q}
                  </button>
                ))}
              </div>
            </div>
          )}

          {messages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} />
          ))}

          {isLoading && (
            <div className="flex items-start gap-3">
              <div className="w-8 h-8 bg-azure-100 rounded-full flex items-center justify-center flex-shrink-0">
                <svg className="w-4 h-4 text-azure-600 animate-pulse" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09Z" />
                </svg>
              </div>
              <div className="bg-gray-100 rounded-xl px-4 py-3 text-sm text-gray-600">
                <div className="flex items-center gap-2">
                  <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-azure-600" />
                  Thinking...
                </div>
              </div>
            </div>
          )}

          <div ref={messagesEndRef} />
        </div>

        {/* Input area */}
        <div className="border-t border-gray-200 p-4">
          <div className="flex gap-3">
            <textarea
              ref={inputRef}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Ask about your cloud resources, changes, or security..."
              rows={1}
              maxLength={4000}
              disabled={isLoading}
              className="flex-1 border border-gray-300 rounded-xl px-4 py-3 text-sm resize-none focus:border-azure-500 focus:outline-none focus:ring-1 focus:ring-azure-500 disabled:opacity-50"
              style={{ minHeight: '44px', maxHeight: '120px' }}
            />
            <button
              onClick={() => sendMessage()}
              disabled={!input.trim() || isLoading}
              className="px-5 py-3 bg-azure-600 text-white rounded-xl text-sm font-medium hover:bg-azure-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex-shrink-0"
            >
              Send
            </button>
          </div>
          <p className="text-xs text-gray-400 mt-2 text-center">
            Read-only and grounded in your workspace data. The Assistant cannot take actions —
            changes go through the approvals workflow.
          </p>
        </div>
      </div>
    </div>
  );
}

function MessageBubble({ message }: { message: ChatMessage }) {
  if (message.role === 'user') {
    return (
      <div className="flex items-start gap-3 justify-end">
        <div className="bg-azure-600 text-white rounded-xl px-4 py-3 text-sm max-w-2xl">
          {message.content}
        </div>
        <div className="w-8 h-8 bg-gray-200 rounded-full flex items-center justify-center flex-shrink-0 text-xs font-medium">
          U
        </div>
      </div>
    );
  }

  if (message.role === 'system') {
    return (
      <div className="text-center">
        <span className="text-xs text-red-500 bg-red-50 px-3 py-1 rounded-full">{message.content}</span>
      </div>
    );
  }

  return (
    <div className="flex items-start gap-3">
      <div className="w-8 h-8 bg-azure-100 rounded-full flex items-center justify-center flex-shrink-0">
        <svg className="w-4 h-4 text-azure-600" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09Z" />
        </svg>
      </div>
      <div className="max-w-2xl space-y-2">
        <div className={`rounded-xl px-4 py-3 text-sm max-w-none prose prose-sm prose-gray [&_table]:w-full [&_table]:text-xs [&_th]:bg-gray-200 [&_th]:px-3 [&_th]:py-1.5 [&_th]:text-left [&_th]:font-semibold [&_td]:px-3 [&_td]:py-1.5 [&_td]:border-t [&_td]:border-gray-200 [&_pre]:bg-gray-200 [&_pre]:rounded-lg [&_code]:text-xs [&_p:last-child]:mb-0 ${
          message.refused ? 'bg-amber-50 text-amber-900 border border-amber-200' : 'bg-gray-100 text-gray-800'
        }`}>
          <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown>
        </div>
        {message.usage && message.usage.totalTokens > 0 && (
          <div className="text-xs text-gray-400 pl-2">{message.usage.totalTokens} tokens</div>
        )}
      </div>
    </div>
  );
}
