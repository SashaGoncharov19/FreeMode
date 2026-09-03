// Compatible with the ScriptHookV SDK "main.h" (see README.md in the parent folder).
// The prototypes below define the mangled import names, so they must match ScriptHookV.dll exactly.
#pragma once

#include <windows.h>

// NOTE: unlike natives.h, the SDK's main.h does not pull in types.h. Keeping it that way matters:
// types.h defines "typedef int Object", which would shadow System::Object in the C++/CLI sources.

#ifdef SHV_COMPAT_EXPORT
#define IMPORT __declspec(dllexport)
#else
#define IMPORT __declspec(dllimport)
#endif

/* keyboard */

typedef void(*KeyboardHandler)(DWORD key, WORD repeats, BYTE scanCode, BOOL isExtended, BOOL isWithAlt, BOOL wasDownBefore, BOOL isUpNow);

IMPORT void keyboardHandlerRegister(KeyboardHandler handler);
IMPORT void keyboardHandlerUnregister(KeyboardHandler handler);

/* scripts */

IMPORT void scriptWait(DWORD time);
IMPORT void scriptRegister(HMODULE module, void(*LP_SCRIPT_MAIN)());
IMPORT void scriptRegisterAdditionalThread(HMODULE module, void(*LP_SCRIPT_MAIN)());
IMPORT void scriptUnregister(HMODULE module);
IMPORT void scriptUnregister(void(*LP_SCRIPT_MAIN)()); // deprecated

static void WAIT(DWORD time) { scriptWait(time); }
static void TERMINATE() { WAIT(MAXDWORD); }

IMPORT void nativeInit(UINT64 hash);
IMPORT void nativePush64(UINT64 val);
IMPORT PUINT64 nativeCall();

IMPORT UINT64 *getGlobalPtr(int globalId);

/* world */

IMPORT int worldGetAllVehicles(int *arr, int arrSize);
IMPORT int worldGetAllPeds(int *arr, int arrSize);
IMPORT int worldGetAllObjects(int *arr, int arrSize);
IMPORT int worldGetAllPickups(int *arr, int arrSize);

/* misc */

IMPORT BYTE *getScriptHandleBaseAddress(int handle);

enum eGameVersion : int
{
	VER_1_0_335_2_STEAM,
	VER_1_0_335_2_NOSTEAM,

	VER_1_0_350_1_STEAM,
	VER_1_0_350_2_NOSTEAM,

	VER_1_0_372_2_STEAM,
	VER_1_0_372_2_NOSTEAM,

	VER_1_0_393_2_STEAM,
	VER_1_0_393_2_NOSTEAM,

	VER_1_0_393_4_STEAM,
	VER_1_0_393_4_NOSTEAM,

	VER_1_0_463_1_STEAM,
	VER_1_0_463_1_NOSTEAM,

	VER_1_0_505_2_STEAM,
	VER_1_0_505_2_NOSTEAM,

	VER_1_0_573_1_STEAM,
	VER_1_0_573_1_NOSTEAM,

	VER_1_0_617_1_STEAM,
	VER_1_0_617_1_NOSTEAM,

	VER_SIZE,
	VER_UNK = -1
};

IMPORT eGameVersion getGameVersion();

/* textures */

IMPORT int createTexture(const char *texFileName);
IMPORT void drawTexture(int id, int index, int level, int time, float sizeX, float sizeY, float centerX, float centerY, float posX, float posY, float rotation, float screenHeightScaleFactor, float r, float g, float b, float a);

/* present callback */

typedef void(*PresentCallback)(void *);

IMPORT void presentCallbackRegister(PresentCallback cb);
IMPORT void presentCallbackUnregister(PresentCallback cb);
