#include "Provider.h"

#include "guid.h"

#include <new>

extern "C" void DllAddRef();
extern "C" void DllRelease();

VestigoProvider::VestigoProvider()
{
    DllAddRef();
}

VestigoProvider::~VestigoProvider()
{
    DllRelease();
    UnAdvise();
    if (credential_ != nullptr)
    {
        credential_->Release();
        credential_ = nullptr;
    }
    if (users_ != nullptr)
    {
        users_->Release();
        users_ = nullptr;
    }
}

IFACEMETHODIMP VestigoProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (ppv == nullptr)
    {
        return E_POINTER;
    }
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
    {
        *ppv = static_cast<ICredentialProvider*>(this);
    }
    else if (riid == IID_ICredentialProviderSetUserArray)
    {
        *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
    }
    else
    {
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

IFACEMETHODIMP_(ULONG) VestigoProvider::AddRef()
{
    return InterlockedIncrement(&refs_);
}

IFACEMETHODIMP_(ULONG) VestigoProvider::Release()
{
    const LONG value = InterlockedDecrement(&refs_);
    if (value == 0)
    {
        delete this;
    }
    return static_cast<ULONG>(value);
}

IFACEMETHODIMP VestigoProvider::SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, DWORD)
{
    if (scenario != CPUS_LOGON && scenario != CPUS_UNLOCK_WORKSTATION)
    {
        return E_NOTIMPL;
    }
    scenario_ = scenario;
    return S_OK;
}

IFACEMETHODIMP VestigoProvider::SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

IFACEMETHODIMP VestigoProvider::Advise(ICredentialProviderEvents* events, UINT_PTR context)
{
    if (events == nullptr)
    {
        return E_INVALIDARG;
    }
    UnAdvise();
    events_ = events;
    events_->AddRef();
    adviseContext_ = context;
    pipe_.Start(scenario_, sids_, &OnDispatch, this);
    return S_OK;
}

IFACEMETHODIMP VestigoProvider::UnAdvise()
{
    pipe_.Stop();
    if (events_ != nullptr)
    {
        events_->Release();
        events_ = nullptr;
    }
    adviseContext_ = 0;
    return S_OK;
}

IFACEMETHODIMP VestigoProvider::GetFieldDescriptorCount(DWORD* count)
{
    if (count == nullptr)
    {
        return E_INVALIDARG;
    }
    *count = 0;
    return S_OK;
}

IFACEMETHODIMP VestigoProvider::GetFieldDescriptorAt(DWORD, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** descriptor)
{
    if (descriptor == nullptr)
    {
        return E_INVALIDARG;
    }
    *descriptor = nullptr;
    return E_INVALIDARG;
}

IFACEMETHODIMP VestigoProvider::GetCredentialCount(DWORD* count, DWORD* defaultIndex, BOOL* autoLogonWithDefault)
{
    if (count == nullptr || defaultIndex == nullptr || autoLogonWithDefault == nullptr)
    {
        return E_INVALIDARG;
    }

    ApplyDispatch();
    if (!hasCredential_)
    {
        *count = 0;
        *defaultIndex = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *autoLogonWithDefault = FALSE;
        return S_OK;
    }

    *count = 1;
    *defaultIndex = 0;
    *autoLogonWithDefault = TRUE;
    return S_OK;
}

IFACEMETHODIMP VestigoProvider::GetCredentialAt(DWORD index, ICredentialProviderCredential** credential)
{
    if (credential == nullptr)
    {
        return E_INVALIDARG;
    }
    *credential = nullptr;
    ApplyDispatch();
    if (index != 0 || credential_ == nullptr)
    {
        return E_INVALIDARG;
    }
    return credential_->QueryInterface(IID_ICredentialProviderCredential, reinterpret_cast<void**>(credential));
}

IFACEMETHODIMP VestigoProvider::SetUserArray(ICredentialProviderUserArray* users)
{
    if (users_ != nullptr)
    {
        users_->Release();
        users_ = nullptr;
    }
    sids_.clear();
    users_ = users;
    if (users_ == nullptr)
    {
        return S_OK;
    }
    users_->AddRef();

    DWORD count = 0;
    if (FAILED(users_->GetCount(&count)))
    {
        return S_OK;
    }
    for (DWORD index = 0; index < count; ++index)
    {
        ICredentialProviderUser* user = nullptr;
        if (FAILED(users_->GetAt(index, &user)) || user == nullptr)
        {
            continue;
        }
        LPWSTR sid = nullptr;
        if (SUCCEEDED(user->GetSid(&sid)) && sid != nullptr)
        {
            sids_.emplace_back(sid);
            CoTaskMemFree(sid);
        }
        user->Release();
    }
    return S_OK;
}

void VestigoProvider::OnDispatch(void* context)
{
    auto* self = static_cast<VestigoProvider*>(context);
    if (self->events_ != nullptr)
    {
        self->events_->CredentialsChanged(self->adviseContext_);
    }
}

void VestigoProvider::ApplyDispatch()
{
    DispatchRequest request;
    if (!pipe_.TryTakeDispatch(request))
    {
        return;
    }

    if (scenario_ == CPUS_UNLOCK_WORKSTATION && !request.targetSid.empty())
    {
        bool match = false;
        for (const std::wstring& sid : sids_)
        {
            if (_wcsicmp(sid.c_str(), request.targetSid.c_str()) == 0)
            {
                match = true;
                break;
            }
        }
        if (!match)
        {
            pipe_.Report(vestigo::protocol::ProviderStatus::Failed, ERROR_NO_SUCH_USER, L"sid_mismatch");
            SecureZeroMemory(request.password.data(), request.password.size() * sizeof(wchar_t));
            return;
        }
    }

    if (credential_ != nullptr)
    {
        credential_->Release();
        credential_ = nullptr;
    }
    credential_ = new (std::nothrow) VestigoCredential();
    if (credential_ == nullptr)
    {
        pipe_.Report(vestigo::protocol::ProviderStatus::Failed, ERROR_OUTOFMEMORY, L"out_of_memory");
        SecureZeroMemory(request.password.data(), request.password.size() * sizeof(wchar_t));
        return;
    }
    credential_->Initialize(scenario_, request.userName, request.domain, std::move(request.password), request.targetSid, &pipe_);
    hasCredential_ = true;
}
