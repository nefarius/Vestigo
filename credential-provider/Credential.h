#pragma once

#include "Platform.h"

#include <credentialprovider.h>

#include <cstdint>
#include <string>

class PipeClient;

class VestigoCredential final : public ICredentialProviderCredential2
{
public:
    VestigoCredential();
    ~VestigoCredential();

    void Initialize(
        CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario,
        const std::wstring& userName,
        const std::wstring& domain,
        std::wstring password,
        const std::wstring& userSid,
        PipeClient* pipe,
        std::uint64_t generation);

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* events) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* autoLogon) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(
        DWORD field,
        CREDENTIAL_PROVIDER_FIELD_STATE* state,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) override;
    IFACEMETHODIMP GetStringValue(DWORD field, LPWSTR* value) override;
    IFACEMETHODIMP GetBitmapValue(DWORD field, HBITMAP* bitmap) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD field, BOOL* checked, LPWSTR* label) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD field, DWORD* adjacent) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD field, DWORD* items, DWORD* selected) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD field, DWORD index, LPWSTR* item) override;
    IFACEMETHODIMP SetStringValue(DWORD field, LPCWSTR value) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD field, BOOL checked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD field, DWORD selected) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD field) override;
    IFACEMETHODIMP GetSerialization(
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization,
        LPWSTR* optionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(
        NTSTATUS status,
        NTSTATUS substatus,
        LPWSTR* optionalStatusText,
        CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon) override;

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(LPWSTR* sid) override;

    NTSTATUS LastStatus() const { return lastStatus_; }

private:
    LONG refs_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario_ = CPUS_INVALID;
    std::wstring userName_;
    std::wstring domain_;
    std::wstring password_;
    std::wstring userSid_;
    PipeClient* pipe_ = nullptr;
    std::uint64_t generation_ = 0;
    NTSTATUS lastStatus_ = 0;
};
