"""ChatGPT-style local web chat, with an optional saved-chat terminal mode."""
import argparse
import asyncio
from pathlib import Path
import sys

sys.path.insert(0,str(Path(__file__).resolve().parent/'src'))
from chat_session import ChatClient, ChatSession, ContextFull
from common import DATA

HELP = '''Type a message and press Enter.
/new [NAME]     Start a NEW empty session; previous session stays saved.
/resume NAME    Resume an existing session with its conversation history.
/sessions       List saved sessions.
/history        Show user and assistant messages in this session.
/tools          Show connected browser/news tools.
/help           Show these instructions.
/quit           Exit; completed turns have already been saved.
Session names use letters, digits, underscores and hyphens.
The full conversation is resent each turn, including tool exchanges.
If it exceeds the context budget, start /new; no older turns are silently dropped.'''

async def cli_main(args):
    directory = DATA/'chat-sessions'
    def listing():
        paths = sorted(directory.glob('*.json'),key=lambda p:p.stat().st_mtime,reverse=True)
        print('\n'.join(p.stem for p in paths) or 'No saved sessions.')
    if args.list:
        listing(); return
    session = ChatSession(args.session)
    try:
        async with ChatClient() as client:
            print(f'Session: {session.name}\nSaved at: {session.path}')
            print('Browser MCP connected. Shared model may queue behind the five-minute agent.')
            if args.message:
                for text in args.message:
                    print('You: '+text)
                    print('Bonsai: '+await client.ask(session,text))
                return
            print(HELP)
            while True:
                try:
                    text = input('\nYou> ').strip()
                except (EOFError,KeyboardInterrupt):
                    print('\nSession saved.'); break
                if not text:
                    continue
                if text in ('/quit','/exit'):
                    break
                try:
                    command,_,value = text.partition(' ')
                    value = value.strip()
                    if command=='/help': print(HELP)
                    elif command=='/sessions': listing()
                    elif command=='/tools': print('\n'.join(sorted(client.names)))
                    elif command=='/history':
                        for m in session.messages:
                            if m['role'] in ('user','assistant') and m.get('content'):
                                print(m['role']+': '+m['content'])
                    elif command in ('/new','/resume'):
                        if command=='/resume' and not value:
                            raise ValueError('Use /resume NAME.')
                        if command=='/new' and value and (directory/(value+'.json')).exists():
                            raise ValueError('Name already exists. Use /resume NAME or choose a different /new name.')
                        replacement = ChatSession(value or None,require_existing=command=='/resume')
                        session.close(); session = replacement
                        print(f'Session: {session.name}\nSaved at: {session.path}')
                    elif text.startswith('/'):
                        print('Unknown command. Use /help.')
                    else:
                        print('[Thinking…]',flush=True)
                        print('\nBonsai> '+await client.ask(session,text),flush=True)
                except Exception as error:
                    print(f'Chat error: {error}\nPreviously completed turns remain saved.')
    finally:
        session.close()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='127.0.0.1', choices=('127.0.0.1', 'localhost', '::1'), help='Local interface to bind (default: 127.0.0.1).')
    parser.add_argument('--port', type=int, default=8000, help='Web port (default: 8000).')
    parser.add_argument('--open', action='store_true', help='Open the website in your default browser after startup.')
    parser.add_argument('--cli', action='store_true', help='Use the original terminal chat instead of the website.')
    parser.add_argument('--session', help='Create or resume a named terminal chat (implies --cli).')
    parser.add_argument('--message', action='append', help='Send a terminal message and exit; repeat for multi-turn testing (implies --cli).')
    parser.add_argument('--list', action='store_true', help='List saved sessions without connecting to the model (implies --cli).')
    args = parser.parse_args()
    if args.cli or args.session or args.message or args.list:
        asyncio.run(cli_main(args))
        return
    if not 1 <= args.port <= 65535:
        parser.error('--port must be between 1 and 65535.')
    try:
        import uvicorn
        from chat_web import create_app
    except ImportError:
        parser.exit(1, 'Install the web dependencies first:\n.\\.venv\\Scripts\\python.exe -m pip install -r requirements.txt\n')
    address = f'http://[{args.host}]:{args.port}' if args.host == '::1' else f'http://{args.host}:{args.port}'
    print(f'Bonsai web chat: {address}\nHistory: {DATA / "chat-sessions"}\nPress Ctrl+C to stop the web server.', flush=True)

    class BrowserServer(uvicorn.Server):
        async def startup(self, *startup_args, **startup_kwargs):
            await super().startup(*startup_args, **startup_kwargs)
            if args.open and self.started:
                import webbrowser
                webbrowser.open(address)

    # A single process owns generation jobs and session locks. Do not enable reload/workers.
    BrowserServer(uvicorn.Config(create_app(), host=args.host, port=args.port, loop='asyncio')).run()


if __name__=='__main__':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8',errors='replace')
    try:
        main()
    except KeyboardInterrupt:
        print('\nStopped. Previously completed turns remain saved.')
    except Exception as error:
        print(f'Could not complete chat: {error}\nIf the model is offline, run:\npowershell -NoProfile -ExecutionPolicy Bypass -File scripts/start-model.ps1',file=sys.stderr)
        raise SystemExit(1)
