// Dummy implementation of the ScriptHookV exports. Only the import library produced from this DLL is
// used (../sdk/lib/ScriptHookV.lib); the DLL itself must never be shipped.
#define SHV_COMPAT_EXPORT
#include "inc/main.h"

void keyboardHandlerRegister(KeyboardHandler) {}
void keyboardHandlerUnregister(KeyboardHandler) {}

void scriptWait(DWORD) {}
void scriptRegister(HMODULE, void(*)()) {}
void scriptRegisterAdditionalThread(HMODULE, void(*)()) {}
void scriptUnregister(HMODULE) {}
void scriptUnregister(void(*)()) {}

void nativeInit(UINT64) {}
void nativePush64(UINT64) {}
PUINT64 nativeCall() { return nullptr; }

UINT64 *getGlobalPtr(int) { return nullptr; }

int worldGetAllVehicles(int *, int) { return 0; }
int worldGetAllPeds(int *, int) { return 0; }
int worldGetAllObjects(int *, int) { return 0; }
int worldGetAllPickups(int *, int) { return 0; }

BYTE *getScriptHandleBaseAddress(int) { return nullptr; }

eGameVersion getGameVersion() { return VER_UNK; }

int createTexture(const char *) { return 0; }
void drawTexture(int, int, int, int, float, float, float, float, float, float, float, float, float, float, float, float) {}

void presentCallbackRegister(PresentCallback) {}
void presentCallbackUnregister(PresentCallback) {}

BOOL WINAPI DllMain(HMODULE, DWORD, LPVOID)
{
	return TRUE;
}
